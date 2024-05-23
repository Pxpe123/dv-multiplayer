using System;
using System.Collections;
using System.Collections.Generic;
using DV.InventorySystem;
using DV.UserManagement;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Packets.Clientbound;
using Multiplayer.Patches.SaveGame;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using static DV.UI.ATutorialsMenuProvider;

namespace Multiplayer.Components.SaveGame;

public class StartGameData_ServerSave : AStartGameData
{
    private SaveGameData saveGameData;

    private ClientboundSaveGameDataPacket packet;

    public void SetFromPacket(ClientboundSaveGameDataPacket packet)
    {
        this.packet = packet.Clone();

        saveGameData = SaveGameManager.MakeEmptySave();
        saveGameData.SetString(SaveGameKeys.Game_mode, packet.GameMode);
        DifficultyToUse = DifficultyDataUtils.GetDifficultyFromJSON(JObject.Parse(packet.SerializedDifficulty), false);

        saveGameData.SetFloat(SaveGameKeys.Player_money, packet.Money);

        saveGameData.SetStringArray(SaveGameKeys.Licenses_Jobs, packet.AcquiredJobLicenses);
        saveGameData.SetStringArray(SaveGameKeys.Licenses_General, packet.AcquiredGeneralLicenses);
        saveGameData.SetStringArray(SaveGameKeys.Garages, packet.UnlockedGarages);
        saveGameData.SetObject(SaveGameKeys.Storage_Inventory, JsonConvert.DeserializeObject<List<StorageItemData>>(packet.StorageInventory));
        saveGameData.SetObject(SaveGameKeys.Storage_World, JsonConvert.DeserializeObject<List<StorageItemData>>(packet.StorageWorld));

        saveGameData.SetBool(SaveGameKeys.Tutorial_01_completed, true);
        saveGameData.SetBool(SaveGameKeys.Tutorial_02_completed, true);
        saveGameData.SetBool(SaveGameKeys.Tutorial_03_completed, true);
        saveGameData.SetBool(SaveGameKeys.Derail_Popup_Shown, true);
        saveGameData.SetBool(SaveGameKeys.Damage_Popup_Shown, true);

        CareerManagerDebtControllerPatch.HasDebt = packet.HasDebt;
    }

    public override void Initialize()
    {
        throw new InvalidOperationException($"Use {nameof(SetFromPacket)} instead!");
    }

    public override SaveGameData GetSaveGameData()
    {
        if (saveGameData == null)
            throw new InvalidOperationException($"{nameof(SetFromPacket)} must be called before {nameof(GetSaveGameData)}!");
        return saveGameData;
    }

    public override IEnumerator DoLoad(Transform playerContainer)
    {
        Transform playerTransform = playerContainer.transform;
        playerTransform.position = PlayerManager.IsPlayerPositionValid(packet.Position) ? packet.Position : LevelInfo.Instance.defaultSpawnPosition;
        playerTransform.eulerAngles = new Vector3(0, packet.Rotation, 0);

        LicenseManager.Instance.LoadData(saveGameData);

        if (saveGameData.GetString(SaveGameKeys.Game_mode) == "FreeRoam")
        {
            LicenseManager.Instance.GrabAllGameModeSpecificUnlockables(SaveGameKeys.Game_mode);
        }
        else {
            StartingItemsController.Instance.AddStartingItems(saveGameData, true);
            //StartingItemsController.Instance.AddItemToInventoryFallback(shovel, false);
        }
        Inventory.Instance.MoneyChanged += Client_OnMoneyChanged;
        // if (packet.Debt_existing_locos != null)
        //     LocoDebtController.Instance.LoadExistingLocosDebtsSaveData(packet.Debt_existing_locos.Select(JObject.Parse).ToArray());
        // if (packet.Debt_deleted_locos != null)
        //     LocoDebtController.Instance.LoadDestroyedLocosDebtsSaveData(packet.Debt_deleted_locos.Select(JObject.Parse).ToArray());
        // if (packet.Debt_existing_jobs != null)
        //     LocoDebtController.Instance.LoadExistingLocosDebtsSaveData(packet.Debt_existing_jobs.Select(JObject.Parse).ToArray());
        // if (packet.Debt_staged_jobs != null)
        //     JobDebtController.Instance.LoadStagedJobsDebtsSaveData(packet.Debt_staged_jobs.Select(JObject.Parse).ToArray());
        // if (!string.IsNullOrEmpty(packet.Debt_existing_jobless_cars))
        //     JobDebtController.Instance.LoadExistingJoblessCarsDebtsSaveData(JObject.Parse(packet.Debt_existing_jobless_cars));
        // if (!string.IsNullOrEmpty(packet.Debt_deleted_jobless_cars))
        //     JobDebtController.Instance.LoadDeletedJoblessCarDebtsSaveData(JObject.Parse(packet.Debt_deleted_jobless_cars));
        // if (!string.IsNullOrEmpty(packet.Debt_insurance))
        //     CareerManagerDebtController.Instance.feeQuota.LoadSaveData(JObject.Parse(packet.Debt_insurance));

        carsAndJobsLoadingFinished = true;
        yield break;
    }

    #region Client
    private static void Client_OnMoneyChanged(double oldAmount, double newAmount)
    {
        NetworkLifecycle.Instance.Client.SendMoney((float)newAmount);
    }
    #endregion

    public override string GetPostLoadMessage()
    {
        return null;
    }

    public override bool ShouldCreateSaveGameAfterLoad()
    {
        return false;
    }

    public override void MakeCurrent(){}
}
