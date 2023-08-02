using System.Collections.Generic;
using System.Linq;
using DV;
using DV.InventorySystem;
using DV.Logic.Job;
using DV.Scenarios.Common;
using DV.ThingTypes;
using DV.WeatherSystem;
using LiteNetLib;
using LiteNetLib.Utils;
using Multiplayer.Components;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Networking.Packets.Clientbound.Train;
using Multiplayer.Networking.Packets.Clientbound.World;
using Multiplayer.Networking.Packets.Common;
using Multiplayer.Networking.Packets.Common.Train;
using Multiplayer.Networking.Packets.Serverbound;
using Multiplayer.Utils;
using UnityEngine;
using UnityModManagerNet;

namespace Multiplayer.Networking.Listeners;

public class NetworkServer : NetworkManager
{
    protected override string LogPrefix => "[Server]";

    private readonly Queue<NetPeer> joinQueue = new();
    private readonly Dictionary<byte, ServerPlayer> serverPlayers = new();
    private readonly Dictionary<byte, NetPeer> netPeers = new();

    public IReadOnlyCollection<ServerPlayer> ServerPlayers => serverPlayers.Values;
    public int PlayerCount => netManager.ConnectedPeersCount;

    private static NetPeer selfPeer => NetworkLifecycle.Instance.Client?.selfPeer;
    private readonly ModInfo[] serverMods;

    public readonly IDifficulty Difficulty;
    private bool IsLoaded;

    public NetworkServer(IDifficulty difficulty, Settings settings) : base(settings)
    {
        Difficulty = difficulty;
        serverMods = ModInfo.FromModEntries(UnityModManager.modEntries);
    }

    public bool Start(int port)
    {
        WorldStreamingInit.LoadingFinished += OnLoaded;
        return netManager.Start(port);
    }

    protected override void Subscribe()
    {
        netPacketProcessor.SubscribeReusable<ServerboundClientLoginPacket, ConnectionRequest>(OnServerboundClientLoginPacket);
        netPacketProcessor.SubscribeReusable<ServerboundClientReadyPacket, NetPeer>(OnServerboundClientReadyPacket);
        netPacketProcessor.SubscribeReusable<ServerboundSaveGameDataRequestPacket, NetPeer>(OnServerboundSaveGameDataRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundPlayerPositionPacket, NetPeer>(OnServerboundPlayerPositionPacket);
        netPacketProcessor.SubscribeReusable<ServerboundPlayerCarPacket, NetPeer>(OnServerboundPlayerCarPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTimeAdvancePacket, NetPeer>(OnServerboundTimeAdvancePacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainSyncRequestPacket>(OnServerboundTrainSyncRequestPacket);
        netPacketProcessor.SubscribeReusable<ServerboundTrainDeleteRequestPacket, NetPeer>(OnServerboundTrainDeleteRequestPacket);
        netPacketProcessor.SubscribeReusable<CommonChangeJunctionPacket, NetPeer>(OnCommonChangeJunctionPacket);
        netPacketProcessor.SubscribeReusable<CommonRotateTurntablePacket, NetPeer>(OnCommonRotateTurntablePacket);
        netPacketProcessor.SubscribeReusable<CommonTrainCouplePacket, NetPeer>(OnCommonTrainCouplePacket);
        netPacketProcessor.SubscribeReusable<CommonTrainUncouplePacket, NetPeer>(OnCommonTrainUncouplePacket);
        netPacketProcessor.SubscribeReusable<CommonHoseConnectedPacket, NetPeer>(OnCommonHoseConnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonHoseDisconnectedPacket, NetPeer>(OnCommonHoseDisconnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonMuConnectedPacket, NetPeer>(OnCommonMuConnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonMuDisconnectedPacket, NetPeer>(OnCommonMuDisconnectedPacket);
        netPacketProcessor.SubscribeReusable<CommonCockFiddlePacket, NetPeer>(OnCommonCockFiddlePacket);
        netPacketProcessor.SubscribeReusable<CommonBrakeCylinderReleasePacket, NetPeer>(OnCommonBrakeCylinderReleasePacket);
        netPacketProcessor.SubscribeReusable<CommonHandbrakePositionPacket, NetPeer>(OnCommonHandbrakePositionPacket);
        netPacketProcessor.SubscribeReusable<CommonSimFlowPacket, NetPeer>(OnCommonSimFlowPacket);
    }

    private void OnLoaded()
    {
        Log($"Server loaded, processing {joinQueue.Count} queued players");
        IsLoaded = true;
        while (joinQueue.Count > 0)
        {
            NetPeer peer = joinQueue.Dequeue();
            if (peer.ConnectionState == ConnectionState.Connected)
                OnServerboundClientReadyPacket(null, peer);
        }
    }

    public bool TryGetServerPlayer(NetPeer peer, out ServerPlayer player)
    {
        return serverPlayers.TryGetValue((byte)peer.Id, out player);
    }

    public bool TryGetNetPeer(byte id, out NetPeer peer)
    {
        return netPeers.TryGetValue(id, out peer);
    }

    #region Net Events

    public override void OnPeerConnected(NetPeer peer)
    { }

    public override void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        byte id = (byte)peer.Id;

        if (WorldStreamingInit.isLoaded)
            SaveGameManager.Instance.UpdateInternalData();

        serverPlayers.Remove(id);
        netPeers.Remove(id);
        netManager.SendToAll(WritePacket(new ClientboundPlayerDisconnectPacket {
            Id = id
        }), DeliveryMethod.ReliableUnordered);
    }

    public override void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            player.Ping = latency;

        ClientboundPingUpdatePacket clientboundPingUpdatePacket = new() {
            Id = (byte)peer.Id,
            Ping = latency
        };

        SendPacketToAll(clientboundPingUpdatePacket, DeliveryMethod.ReliableUnordered, peer);

        SendPacket(peer, new ClientboundTickSyncPacket {
            ServerTick = NetworkLifecycle.Instance.Tick
        }, DeliveryMethod.ReliableUnordered);
    }

    public override void OnConnectionRequest(ConnectionRequest request)
    {
        netPacketProcessor.ReadAllPackets(request.Data, request);
    }

    #endregion

    #region Packet Senders

    private void SendPacketToAll<T>(T packet, DeliveryMethod deliveryMethod) where T : class, new()
    {
        NetDataWriter writer = WritePacket(packet);
        foreach (KeyValuePair<byte, NetPeer> kvp in netPeers)
            kvp.Value.Send(writer, deliveryMethod);
    }

    private void SendPacketToAll<T>(T packet, DeliveryMethod deliveryMethod, NetPeer excludePeer) where T : class, new()
    {
        NetDataWriter writer = WritePacket(packet);
        foreach (KeyValuePair<byte, NetPeer> kvp in netPeers)
        {
            if (kvp.Key == excludePeer.Id)
                continue;
            kvp.Value.Send(writer, deliveryMethod);
        }
    }

    public void SendGameParams(GameParams gameParams)
    {
        SendPacketToAll(ClientboundGameParamsPacket.FromGameParams(gameParams), DeliveryMethod.ReliableOrdered, selfPeer);
    }

    public void SendSpawnTrainCar(TrainCar trainCar)
    {
        SendPacketToAll(ClientboundSpawnTrainCarPacket.FromTrainCar(trainCar), DeliveryMethod.ReliableOrdered, selfPeer);
    }

    public void SendDestroyTrainCar(TrainCar trainCar)
    {
        SendPacketToAll(new ClientboundDestroyTrainCarPacket {
            NetId = trainCar.GetNetId()
        }, DeliveryMethod.ReliableOrdered, selfPeer);
    }

    public void SendPhysicsUpdate(TrainCar trainCar, ushort netId, Bogie bogie1, Bogie bogie2, bool bogieTracksDirty, int bogie1TrackDirection, int bogie2TrackDirection)
    {
        SendPacketToAll(new ClientboundTrainPhysicsPacket {
            NetId = netId,
            Tick = NetworkLifecycle.Instance.Tick,
            Speed = trainCar.GetForwardSpeed(),
            Bogie1 = BogieMovementData.FromBogie(bogie1, bogieTracksDirty, bogie1TrackDirection),
            Bogie2 = BogieMovementData.FromBogie(bogie2, bogieTracksDirty, bogie2TrackDirection)
        }, bogieTracksDirty ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, selfPeer);
    }

    public void SendCargoState(TrainCar trainCar, ushort netId, bool isLoading, byte cargoModelIndex)
    {
        Car logicCar = trainCar.logicCar;
        CargoType cargoType = isLoading ? logicCar.CurrentCargoTypeInCar : logicCar.LastUnloadedCargoType;
        SendPacketToAll(new ClientboundCargoStatePacket {
            NetId = netId,
            IsLoading = isLoading,
            CargoType = (ushort)cargoType,
            CargoAmount = logicCar.LoadedCargoAmount,
            CargoModelIndex = cargoModelIndex,
            WarehouseMachineId = logicCar.CargoOriginWarehouse?.ID
        }, DeliveryMethod.ReliableOrdered, selfPeer);
    }

    public void SendCarHealthUpdate(ushort netId, float health)
    {
        SendPacketToAll(new ClientboundCarHealthUpdatePacket {
            NetId = netId,
            Health = health
        }, DeliveryMethod.ReliableOrdered, selfPeer);
    }

    #endregion

    #region Listeners

    private void OnServerboundClientLoginPacket(ServerboundClientLoginPacket packet, ConnectionRequest request)
    {
        Log($"Processing login packet{(Multiplayer.Settings.LogIps ? $" from {request.RemoteEndPoint.Address}" : "")}");

        if (Multiplayer.Settings.Password != packet.Password)
        {
            LogWarning("Denied login due to invalid password!");
            ClientboundServerDenyPacket denyPacket = new() {
                Reason = "Invalid password!"
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        if (packet.BuildMajorVersion != BuildInfo.BUILD_VERSION_MAJOR)
        {
            LogWarning($"Denied login to incorrect game version! Got: {packet.BuildMajorVersion}, expected: {BuildInfo.BUILD_VERSION_MAJOR}");
            ClientboundServerDenyPacket denyPacket = new() {
                Reason = $"Game version mismatch! Server build: {BuildInfo.BUILD_VERSION_MAJOR}, your build: {packet.BuildMajorVersion}."
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        if (netManager.ConnectedPeersCount >= Multiplayer.Settings.MaxPlayers)
        {
            LogWarning("Denied login due to server being full!");
            ClientboundServerDenyPacket denyPacket = new() {
                Reason = "Server is full!"
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        ModInfo[] clientMods = packet.Mods;
        if (!serverMods.SequenceEqual(clientMods))
        {
            ModInfo[] missing = serverMods.Except(clientMods).ToArray();
            ModInfo[] extra = clientMods.Except(serverMods).ToArray();
            LogWarning($"Denied login due to mod mismatch! {missing.Length} missing, {extra.Length} extra");
            ClientboundServerDenyPacket denyPacket = new() {
                Reason = "Mod mismatch!",
                Missing = missing,
                Extra = extra
            };
            request.Reject(WritePacket(denyPacket));
            return;
        }

        NetPeer peer = request.Accept();

        ServerPlayer serverPlayer = new() {
            Id = (byte)peer.Id,
            Username = packet.Username
        };

        serverPlayers.Add(serverPlayer.Id, serverPlayer);
    }

    private void OnServerboundSaveGameDataRequestPacket(ServerboundSaveGameDataRequestPacket packet, NetPeer peer)
    {
        if (netPeers.ContainsKey((byte)peer.Id))
        {
            LogWarning("Denied save game data request from already connected peer!");
            return;
        }

        TryGetServerPlayer(peer, out ServerPlayer player);

        SendPacket(peer, ClientboundGameParamsPacket.FromGameParams(Globals.G.GameParams), DeliveryMethod.ReliableOrdered);
        SendPacket(peer, ClientboundSaveGameDataPacket.CreatePacket(player), DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundClientReadyPacket(ServerboundClientReadyPacket packet, NetPeer peer)
    {
        byte peerId = (byte)peer.Id;

        // Allow clients to connect before the server is fully loaded
        if (!IsLoaded)
        {
            joinQueue.Enqueue(peer);
            SendPacket(peer, new ClientboundServerLoadingPacket(), DeliveryMethod.ReliableOrdered);
            return;
        }

        // Unpause physics
        if (AppUtil.Instance.IsTimePaused)
            AppUtil.Instance.RequestSystemOnValueChanged(0.0f);

        // Allow the player to receive packets
        netPeers.Add(peerId, peer);

        // Send the new player to all other players
        ServerPlayer serverPlayer = serverPlayers[peerId];
        ClientboundPlayerJoinedPacket clientboundPlayerJoinedPacket = new() {
            Id = peerId,
            Username = serverPlayer.Username
        };
        SendPacketToAll(clientboundPlayerJoinedPacket, DeliveryMethod.ReliableOrdered, peer);

        Log($"Client {peer.Id} is ready. Sending world state");

        // No need to sync the world state if the player is the host
        if (NetworkLifecycle.Instance.IsHost(peer))
        {
            SendPacket(peer, new ClientboundRemoveLoadingScreenPacket(), DeliveryMethod.ReliableOrdered);
            return;
        }

        SendPacket(peer, new ClientboundBeginWorldSyncPacket(), DeliveryMethod.ReliableOrdered);

        // Send existing players
        foreach (ServerPlayer player in serverPlayers.Values)
        {
            if (player.Id == peer.Id)
                continue;
            SendPacket(peer, new ClientboundPlayerJoinedPacket {
                Id = player.Id,
                Username = player.Username
            }, DeliveryMethod.ReliableOrdered);
            SendPacket(peer, new ClientboundPlayerCarPacket {
                Id = player.Id,
                CarId = player.Car == null ? ushort.MaxValue : player.Car.GetNetId()
            }, DeliveryMethod.ReliableOrdered);
        }

        // Send weather state
        SendPacket(peer, WeatherDriver.Instance.GetSaveData().ToObject<ClientboundWeatherPacket>(), DeliveryMethod.ReliableOrdered);

        // Send junctions
        SendPacket(peer, new ClientboundJunctionStatePacket {
            selectedBranches = WorldData.Instance.OrderedJunctions.Select(j => (byte)j.selectedBranch).ToArray()
        }, DeliveryMethod.ReliableOrdered);

        // Send turntables
        SendPacket(peer, new ClientboundTurntableStatePacket {
            rotations = TurntableController.allControllers.Select(c => c.turntable.currentYRotation).ToArray()
        }, DeliveryMethod.ReliableOrdered);

        // Send trains
        foreach (TrainCar trainCar in CarSpawner.Instance.allCars)
        {
            if (!trainCar.gameObject.activeInHierarchy)
                continue;
            SendPacket(peer, ClientboundSpawnTrainCarPacket.FromTrainCar(trainCar), DeliveryMethod.ReliableOrdered);
        }

        // All data has been sent, allow the client to load into the world.
        SendPacket(peer, new ClientboundRemoveLoadingScreenPacket(), DeliveryMethod.ReliableOrdered);
    }

    private void OnServerboundPlayerPositionPacket(ServerboundPlayerPositionPacket packet, NetPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
            player.Position = packet.Position + WorldMover.currentMove;

        ClientboundPlayerPositionPacket clientboundPacket = new() {
            Id = (byte)peer.Id,
            Position = packet.Position,
            MoveDir = packet.MoveDir,
            RotationY = packet.RotationY,
            IsJumpingIsOnCar = packet.IsJumpingIsOnCar
        };

        SendPacketToAll(clientboundPacket, DeliveryMethod.Sequenced, peer);
    }

    private void OnServerboundPlayerCarPacket(ServerboundPlayerCarPacket packet, NetPeer peer)
    {
        if (TryGetServerPlayer(peer, out ServerPlayer player))
        {
            TrainComponentLookup.Instance.TrainFromNetId(packet.CarId, out TrainCar trainCar);
            player.Car = trainCar;
        }

        ClientboundPlayerCarPacket clientboundPacket = new() {
            Id = (byte)peer.Id,
            CarId = packet.CarId
        };

        SendPacketToAll(clientboundPacket, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnServerboundTimeAdvancePacket(ServerboundTimeAdvancePacket packet, NetPeer peer)
    {
        SendPacketToAll(new ClientboundTimeAdvancePacket {
            amountOfTimeToSkipInSeconds = packet.amountOfTimeToSkipInSeconds
        }, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonChangeJunctionPacket(CommonChangeJunctionPacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonRotateTurntablePacket(CommonRotateTurntablePacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnCommonTrainCouplePacket(CommonTrainCouplePacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonTrainUncouplePacket(CommonTrainUncouplePacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonHoseConnectedPacket(CommonHoseConnectedPacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonHoseDisconnectedPacket(CommonHoseDisconnectedPacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonMuConnectedPacket(CommonMuConnectedPacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonMuDisconnectedPacket(CommonMuDisconnectedPacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonCockFiddlePacket(CommonCockFiddlePacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonBrakeCylinderReleasePacket(CommonBrakeCylinderReleasePacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableUnordered, peer);
    }

    private void OnCommonHandbrakePositionPacket(CommonHandbrakePositionPacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnCommonSimFlowPacket(CommonSimFlowPacket packet, NetPeer peer)
    {
        SendPacketToAll(packet, DeliveryMethod.ReliableOrdered, peer);
    }

    private void OnServerboundTrainSyncRequestPacket(ServerboundTrainSyncRequestPacket packet)
    {
        if (TrainComponentLookup.Instance.NetworkedTrainFromNetId(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            networkedTrainCar.Server_DirtyAllState();
    }

    private void OnServerboundTrainDeleteRequestPacket(ServerboundTrainDeleteRequestPacket packet, NetPeer peer)
    {
        if (!TryGetServerPlayer(peer, out ServerPlayer player))
            return;

        if (!TrainComponentLookup.Instance.NetworkedTrainFromNetId(packet.NetId, out NetworkedTrainCar networkedTrainCar))
            return;
        if (networkedTrainCar.HasPlayers)
        {
            LogWarning($"{player.Username} tried to delete a train with players in it!");
            return;
        }

        TrainCar trainCar = networkedTrainCar.TrainCar;
        float cost = trainCar.playerSpawnedCar ? 0.0f : Mathf.RoundToInt(Globals.G.GameParams.DeleteCarMaxPrice);
        if (Inventory.Instance.PlayerMoney < cost)
        {
            LogWarning($"{player.Username} tried to delete a train without enough money to do so!");
            return;
        }

        Job job = JobsManager.Instance.GetJobOfCar(trainCar);
        switch (job.State)
        {
            case JobState.Available:
                job.ExpireJob();
                break;
            case JobState.InProgress:
                JobsManager.Instance.AbandonJob(job);
                break;
        }

        CarSpawner.Instance.DeleteCar(trainCar);
    }

    #endregion
}
