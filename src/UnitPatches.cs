using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Controllers;
using Controllers.Sickness;
using EventsSystem;
using Gameplay.Immigrants;
using Gameplay.Military;
using Gameplay.Rebuilding;
using Gameplay.Simulators;
using Gameplay.Units;
using Gameplay.Units.Characters;
using Gameplay.Units.Combat.Projectiles;
using Gameplay.Units.Enemy;
using Gameplay.Units.Enemy.Hideouts;
using Gameplay.Units.Spawning;
using Gameplay.Units.Swarms;
using Gameplay.Units.Virtual;
using Gameplay.Vehicles;
using Gameplay.GameResources;
using Gameplay.Units.Interfaces;
using Gameplay.Units.Skills;
using Gameplay.Scavenge;
using HarmonyLib;
using IfzCoop.Sync;
using UnityEngine;

namespace IfzCoop
{
    /// <summary>
    /// Patches für den Einheiten-Abgleich. Jeder Haken einzeln: fehlt eine Methode (Spiel-Update),
    /// fällt nur dieser Teil aus und das Log sagt welcher; der restliche Mod läuft weiter.
    ///
    /// Gast (Marionetten-Modus): eigene KI, Spawner und Schaden aus. Was vom Host kommt, läuft mit
    /// UnitSync.NetApplying trotzdem durch.
    /// </summary>
    internal static class UnitPatches
    {
        public static int Applied { get; private set; }
        public static int Missing { get; private set; }

        private static UnitSync Units => Plugin.Instance?.Session?.Units;
        private static bool Puppet => Units != null && Units.PuppetMode;

        public static void Apply(Harmony harmony)
        {
            // Marionetten-Takt statt eigener Gruppen-Logik
            TryPatch(harmony, typeof(Group), "CustomUpdate", nameof(GroupUpdatePrefix));
            TryPatch(harmony, typeof(Group), "FixedUpdate", nameof(BlockWhenPuppet));

            // KI und Spawner, die Gruppen erzeugen oder steuern
            TryPatch(harmony, typeof(GroupSpawnPointsController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(HideoutsController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(SwarmGroupsToLairConverter), "CustomUpdate", nameof(BlockWhenPuppet));
            // Horde → Höhle nur beim Host. Beim Laden (SaveHandler.LoadingFinished → ConvertAfterGameLoaded) baute der Gast
            // sonst eigene Höhlen mit eigener ID und eigener Zufallsgröße; die passten nie zu denen des Hosts
            // (verschiedene Anzahl in der Höhle). Beim Laden ist der Gast noch nicht „im Spiel“, darum eigene Sperre.
            TryPatch(harmony, typeof(SwarmGroupsToLairConverter), "TryConvertToLairInBuilding", nameof(BlockWhenGuest));
            TryPatch(harmony, typeof(HideoutsController), "TryConvertToHideout", nameof(BlockWhenGuest));
            TryPatch(harmony, typeof(ImmigrantsSpawner), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(EmmigrantsController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(MilitaryBackupController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(SimulatorsController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(VirtualGroupsController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Swarm), "Update", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(DefaultSpawnPointSpawner), "SpawnPoints", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(SicknessController), "UpdateSickness", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(SwarmSpawner), "SpawnSwarm", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(VehiclesController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(EventsSystemController), "SpawnGroupFromAction", nameof(BlockCoroutineWhenPuppet));
            // Ereignissystem nur beim Host (Paket F, EventSync): kein eigener Takt, keine Aktionen, keine eigenen
            // Ereignisse/Missionen; Ziele und Missionsende nur, wenn sie vom Host kommen.
            TryPatch(harmony, typeof(EventsSystemController), "Update", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(EventsSystemController), "RunAction", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(EventsSystemController), "ForceEventRun", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(EventsSystemController), "StartMission", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(EventsSystemController), "EndMission", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(EventsSystem.Objectives.Objective), "Complete", nameof(BlockWhenPuppet));
            // Forschung, Gesetze, Stimmung, Wetter nur beim Host (Paket G, WorldSync); Klicks des Gastes gehen an den Host.
            TryPatch(harmony, typeof(Gameplay.Research.ResearchController), "AddTechnologyResearchProgress", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Research.ResearchController), "AddScientificMaterials", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Research.ResearchController), "HandleResearchButtonClick", nameof(ResearchStartPrefix));
            TryPatch(harmony, typeof(Gameplay.Research.ResearchController), "CancelCurrentResearch", nameof(ResearchCancelPrefix));
            TryPatch(harmony, typeof(Gameplay.Laws.Law), "SetEnabled", nameof(LawSetPrefix));
            TryPatch(harmony, typeof(Gameplay.Laws.LawsController), "StartGathering", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Laws.LawsController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Mood.MoodController), "CustomUpdate", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Controllers.Weather.WeatherController), "UpdateForecast", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Controllers.Weather.WeatherController), "AddForecastProgress", nameof(BlockWhenPuppet));
            // Bevölkerung nur beim Host (Paket I, PopulationSync): keine eigenen Geburten und neuen Arbeiter
            TryPatch(harmony, typeof(Controllers.CharacterLogic.CitizensController), "IncreasePopulation", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Controllers.CharacterLogic.CitizensController), "MakeBabies", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Player.Workers.WorkersController), "CreateNewWorkers", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Player.Workers.WorkersController), "CreateNewWorkersAsync", nameof(BlockCoroutineWhenPuppet));
            // Arbeitszuteilung nur beim Host (WorkSync): eigene Verteilung des Gastes aus, Klicks gehen an den Host
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "AssignWorkers", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "AssignWorker", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "FindWork", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "ReplaceDeadWorker", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "UnsuspendWork", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "SuspendWork", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "OnPriorityChange", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "StartAlarm", nameof(AlarmStartPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorkController), "StopAlarm", nameof(AlarmStopPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorksPriorityManager), "FindWork", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.WorksPriorityManager), "RealignWorkers", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(UI.InfoPanels.WorkersCountChanger), "Add", nameof(WcAdd));
            TryPatch(harmony, typeof(UI.InfoPanels.WorkersCountChanger), "Remove", nameof(WcRemove));
            TryPatch(harmony, typeof(UI.InfoPanels.WorkersCountChanger), "AddMaxWorkers", nameof(WcAddMax));
            TryPatch(harmony, typeof(UI.InfoPanels.WorkersCountChanger), "RemoveAllWorkers", nameof(WcRemoveAll));
            TryPatch(harmony, typeof(UI.InfoPanels.WorkersCountChanger), "IncreaseMaxWorkers", nameof(WcIncMax));
            TryPatch(harmony, typeof(UI.InfoPanels.WorkersCountChanger), "DecreaseMaxWorkers", nameof(WcDecMax));
            TryPatch(harmony, typeof(UI.InfoPanels.WorkersCountChanger), "OnPriorityPressed", nameof(WcPriority));
            TryPatch(harmony, typeof(global::WorkersLabel), "ZeroMaxWorkers", nameof(LbZero));
            TryPatch(harmony, typeof(global::WorkersLabel), "MaxMaxWorkers", nameof(LbMax));
            TryPatch(harmony, typeof(global::WorkersLabel), "AddWorker", nameof(LbAdd));
            TryPatch(harmony, typeof(global::WorkersLabel), "RemoveWorker", nameof(LbRemove));
            TryPatch(harmony, typeof(global::WorkersLabel), "AddMaxWorkers", nameof(LbAddMax));
            TryPatch(harmony, typeof(global::WorkersLabel), "RemoveAllWorkers", nameof(LbRemoveAll));
            TryPatch(harmony, typeof(global::WorkersLabel), "OnPriorityPressed", nameof(LbPriority));
            // Felder der Umgebungskarte rechnet der Host (ExpeditionSync): beim Gast kein eigenes Durchsuchen, keine gewürfelten Kisten,
            // kein Aufheben, keine Horde beim Betreten, kein Fahrzeug-Fund am Ende, keine Ereignis-Auslöser durch Fortschritt
            TryPatch(harmony, typeof(global::Expedition.ExpeditionTilerTile), "TryInitializeScavengeWork", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(global::Expedition.ExpeditionTilerTile), "Scavenge", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(global::Expedition.ExpeditionTilerTile), "TryTakeResourceCrate", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(global::Expedition.ExpeditionHordeSpawner), "OnSquadEnterTile", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(global::Expedition.VehiclesSpawner), "OnScavengeExpeditionTile", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(global::ExpeditionScavengeController), "OnExpeditionTileScavengeUpdate", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(global::EventsSystem.EventTriggerManager), "OnExpeditionTileScavengeUpdate", nameof(BlockAlwaysWhenPuppet));
            // Händler (TraderSync): Abklingzeit, Würfeln der Vorräte und Liefer-Listen nur beim Host;
            // Knöpfe und Ziehen im Tauschfenster des Gastes gehen an den Host (vor ExchangeSync registriert)
            TryPatch(harmony, typeof(Gameplay.Trading.VendorController), "OnHourChanged", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Trading.VendorController), "FillResourcesContainer", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Trading.VendorController), "AddSupplyResources", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Trading.VendorController), "SetVendorCooldown", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Trading.VendorController), "ResetVendorCooldown", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Trading.VendorController), "SetLastOfferAttractiveness", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Trade.TradingController), "AcceptOffer", nameof(TradeAcceptPrefix));
            TryPatch(harmony, typeof(Trade.TradingController), "CancelOffer", nameof(TradeCancelPrefix));
            TryPatch(harmony, typeof(Trade.TradingController), "ThreatenOffer", nameof(TradeThreatenPrefix));
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.RegularDragStrategy), "PlaceResource", nameof(TradeDragPlacePrefix));
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.RegularDragStrategy), "SwapResource", nameof(TradeDragSwapPrefix));
            // Arbeiter-Figuren beim Gast als Marionetten (WorkerPosSync): Takt statt eigener Bewegung, keine eigene KI,
            // keine eigene Arbeit (Baufortschritt), keine Griffe ins Lager, Kiste in der Hand verschwindet beim Tod
            TryPatch(harmony, typeof(Character), "CustomUpdate", nameof(WorkerPosUpdatePrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.AI.WorkerBehaviour), "Tick", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkModule), "ExecuteWork", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Player.ResourceInHandModule), "GetResourceFrom", nameof(WorkerStockPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Player.ResourceInHandModule), "ReturnResourceTo", nameof(WorkerStockPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Player.ResourceInHandModule), "ReturnResourceToStructure", nameof(WorkerStockPrefix));
            TryPatch(harmony, typeof(Character), "DestroyCharacter", nameof(WorkerPosDestroyPrefix));
            // Gebiets-Aufträge (AreaWorkSync): Anlegen/Löschen des Gastes als Wunsch an den Host, eigene Sammel-/Pflanzarbeit
            // des Gastes aus (Fortschritt, Fällen, Pflanzen kommen vom Host), verschwundene Sammelobjekte des Hosts melden
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.AreaWorkSystem.AreaWorkController), "CreateAreaWork", nameof(AreaCreatePrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.AggregateWork), "DeleteWork", nameof(AreaDeletePrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.Works.GatherResourcesWork), "Execute", nameof(AreaExecutePrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.PlantTreeWork), "Execute", nameof(AreaExecutePrefix));
            TryPatch(harmony, typeof(Controllers.GatherableObjectContainer), "RemoveGatherable", null, nameof(GatherableGonePostfix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.AreaWork), "UpdateProfit", null, nameof(AreaProfitPostfix));
            // Speichern des Hosts: Gast bekommt einen Hinweis (das Spiel steht dabei kurz)
            TryPatch(harmony, typeof(SaveHandler), "SaveGame", null, nameof(SaveGamePostfix));
            // Produktion, Werkstatt, Sägewerk, Reparaturtrupp, Anpassen (ProductionSync): Wirkung nur beim Host, Klicks des Gastes als Wunsch
            TryPatch(harmony, typeof(UI.InfoPanels.NestedUIElements.ProductionPanel), "OnProductionBarClicked", nameof(ProdBarPrefix));
            TryPatch(harmony, typeof(Gameplay.Production.ProductionWork), "ProduceResource", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Production.ProductionWork), "ConsumeCostResources", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.CarWorkshopController), "CreateConstructionRequestFor", nameof(ShopBuildPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.CarWorkshopController), "CreateModificationRequestFor", nameof(ShopModifyPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.CarWorkshopController), "CreateDeconstructionRequestFor", nameof(ShopScrapPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.CarWorkshopController), "HandleRequestCanceled", nameof(ShopCancelPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.CarWorkshopController), "HandleRequestCompleted", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Research.CarWorkshopWork), "CheckRequestComplete", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Buildings.CarWorkshopHandler), "AddRequest", nameof(ShopAddPrefix));
            TryPatch(harmony, typeof(Gameplay.Buildings.CarWorkshopHandler), "SelectRequest", nameof(ShopSelectPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.SawmillWork), "SetPlayerCondition", nameof(SawPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.HandymanWork), "ChangeSectionPriority", nameof(HandyPrefix));
            TryPatch(harmony, typeof(Controllers.CustomizeBuildingController), "OnConstructionConfirmed", nameof(CustomizePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.StateMachine.States.BuildingCustomizationState), "Enter", nameof(CustomEnterPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.StateMachine.States.BuildingCustomizationState), "HandleCustomizationEnd", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.StateMachine.States.BuildingCustomizationState), "HandleRepairEnd", nameof(BlockAlwaysWhenPuppet));
            // Einwanderer nur beim Host (StorySync): der Gast entdeckt, nimmt an, lehnt ab und wandelt nicht selbst um;
            // Funk-Antworten gehen über RadioSync, Einladen per Funk und Seiten/Story-Kamera über StorySync.
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "ImmigrantsSpottedSignal", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "OnImmigrantsReachedHq", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "AcceptSpecificImmigrants", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "DeclineSpecificImmigrant", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "ChangeSpecificGroupAffiliation", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "AcceptImmigrants", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "DeclineImmigrants", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "ChangeImmigrantsAffiliation", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "SpawnImmigrantsAccepted", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Immigrants.ImmigrantsController), "SpawnImmigrantsAcceptedAtMarker", nameof(BlockAlwaysWhenPuppet));
            // Einladen bei Sonnenaufgang (Gesetz „Einwanderer annehmen“) macht nur der Host
            TryPatch(harmony, typeof(TransmissionSystem.TransmissionManager), "OnHourChanged", nameof(BlockAlwaysWhenPuppet));
            StorySync.ApplyPatches(harmony);
            // Gebaute Anlagen (StructureSync): Platzieren, Bau abbrechen, Abriss, Reparatur des Gastes gehen an den Host;
            // eigenes Fertigwerden einer Baustelle beim Gast gesperrt (der Host meldet es)
            TryPatch(harmony, typeof(Gameplay.Rebuilding.PlaceableProductionObject), "OnCursorReleased", nameof(StructPlacePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Farmlands.AreaStructure), "OnCursorReleased", nameof(StructPlacePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Towers.Tower), "OnCursorReleased", nameof(StructPlacePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Gates.Gate), "OnCursorReleased", nameof(StructPlacePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Walls.WallConstructor), "End", nameof(StructWallEndPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Structure), "StartDeconstruction", nameof(StructDeconstructPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Structure), "StartRepairing", nameof(StructRepairPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Walls.Wall), "StartRepairing", nameof(StructRepairPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.StructureInProgressPanelInfo), "OnCancelButtonClicked", nameof(StructPanelCancelPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.StructureInProgressPanelInfo), "HandleInfoPanelAction", nameof(StructPanelActionPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.StructureInRepairPanelInfo), "OnCancelButtonClicked", nameof(StructPanelCancelPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.StructureInRepairPanelInfo), "HandleInfoPanelAction", nameof(StructPanelActionPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.RuinedStructurePanelInfo), "OnCancelButtonClicked", nameof(StructPanelCancelPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.RuinedStructurePanelInfo), "HandleInfoPanelAction", nameof(StructPanelActionPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.DemolishWorkPanel), "OnDeleted", nameof(StructPanelCancelPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.DemolishWorkPanel), "HandleInfoPanelAction", nameof(StructPanelActionPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Structure), "OnConstructionDone", nameof(StructDonePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.PlaceableProductionObject), "OnConstructionDone", nameof(StructDonePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Farmlands.AreaStructure), "OnConstructionDone", nameof(StructDonePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Farmlands.Farmland), "OnConstructionDone", nameof(StructDonePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Gates.Gate), "OnConstructionDone", nameof(StructDonePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Towers.Tower), "OnConstructionDone", nameof(StructDonePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Walls.Wall), "OnConstructionDone", nameof(StructDonePrefix));
            // Trupp-/Fahrzeug-Fenster, Teilen, Mitglieder ziehen, Wegwerfen des Gastes gehen an den Host (SquadActionSync);
            // Erfahrung und Fähigkeiten steigen nur beim Host, leere Trupps meldet der Host
            TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), "LeaveVehicleInvoke", nameof(SaLeavePrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), "StartRepair", nameof(SaRepairPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), "PourFuelToTrunk", nameof(SaPourPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), "ChangeFuelInState", nameof(SaFuelInPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), "ChangeFuelOutState", nameof(SaFuelOutPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), "HandleSquadLockStateChanged", nameof(SaLockSquadPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), "HandleVehicleLockStateChanged", nameof(SaLockVehiclePrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.AbandonedVehiclePanelInfo), "StartRepair", nameof(SaAbRepairPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.VehicleInRepairPanel), "OnCancelButtonClicked", nameof(SaCancelRepairPrefix));
            TryPatch(harmony, typeof(UI.InfoPanels.VehicleInRepairPanel), "HandleInfoPanelAction", nameof(SaRepairActionPrefix));
            TryPatch(harmony, typeof(UI.VehicleAmmoContainerPanel), "HandleButtonClicked", nameof(SaAmmoPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.SquadSplitPreparer), "PrepareSquadForSplit", nameof(SaSplitPrefix));
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.MembersDragStrategy), "PlaceMember", nameof(SaPlacePrefix), nameof(SaPlacePostfix));
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.MembersDragStrategy), "SwapMembers", nameof(SaSwapPrefix), nameof(SaSwapPostfix));
            TryPatch(harmony, typeof(Controllers.Exchange.ExchangeWithSquadOffer), "InterruptExchange", nameof(SaExchangeEndPrefix), nameof(SaExchangeEndPostfix));
            TryPatch(harmony, typeof(Group), "SetGroupAsEmpty", nameof(SaEmptyPrefix), nameof(SaEmptyPostfix));
            TryPatch(harmony, typeof(Gameplay.Units.Skills.KnowledgeGainer), "Increase", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Skills.KnowledgeGainer), "IncreaseWithKill", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Skills.KnowledgeGainer), "IncreaseInShootingRange", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Skills.KnowledgeActivity), "IncreaseWithValue", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Gameplay.Units.Skills.SkillsHandler), "HandleActivityFull", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Controllers.ResourceDiscardController), "HandleDiscardClicked", nameof(SaDiscardPrefix));
            // Siedlungen würfelt nur der Host, umziehen kann nur der Host, Statistik vom Host (ZoneSync)
            ZoneSync.ApplyPatches(harmony);
            WarningSync.ApplyPatches(harmony);
            TowerWeaponSync.ApplyPatches(harmony);
            // Häuser Teil 2 (BuildingSync): Abbrechen/Anhalten/Fortsetzen, Sprengstoff, Beanspruchen, Abbau, eigene Gebäude
            // und Teilen des Gastes gehen an den Host; der Gast teilt nur auf Ansage des Hosts
            TryPatch(harmony, typeof(UI.InfoPanels.StructureInProgressPanelInfo), "OnCancelButtonClicked", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.StructureInProgressPanelInfo), "HandleInfoPanelAction", nameof(BldPanelAction));
            TryPatch(harmony, typeof(UI.InfoPanels.StructureInRepairPanelInfo), "OnCancelButtonClicked", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.StructureInRepairPanelInfo), "HandleInfoPanelAction", nameof(BldPanelAction));
            TryPatch(harmony, typeof(UI.InfoPanels.RuinedStructurePanelInfo), "OnCancelButtonClicked", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.RuinedStructurePanelInfo), "HandleInfoPanelAction", nameof(BldPanelAction));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.DemolishWorkPanel), "OnDeleted", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.DemolishWorkPanel), "OnResumed", nameof(BldPanelResume));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.DemolishWorkPanel), "HandleInfoPanelAction", nameof(BldPanelAction));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.DeadaptWorkPanel), "OnDeleted", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.DeadaptWorkPanel), "OnResumed", nameof(BldPanelResume));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.DeadaptWorkPanel), "HandleInfoPanelAction", nameof(BldPanelAction));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.SplitWorkPanel), "OnCancelButtonClicked", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.WorksPanels.SplitWorkPanel), "HandleInfoPanelAction", nameof(BldPanelAction));
            TryPatch(harmony, typeof(UI.InfoPanels.CustomizationProcessPanel), "OnCancelButtonClicked", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.BuildingCreationPanelInfo), "OnCancelButtonClicked", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.StructurePlantingExplosivesPanelInfo), "OnCancelButtonClicked", nameof(BldPanelCancel));
            TryPatch(harmony, typeof(UI.InfoPanels.NewStructurePanelInfo), "OnPlantExplosivesCallback", nameof(BldExplosivesPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Structure), "MakePartOfZone", nameof(BldClaimPrefix));
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "StartDeconstruction", nameof(BldDeconstructPrefix));
            TryPatch(harmony, typeof(Controllers.CreationBuildingController), "OnConstructionConfirmed", nameof(BldCreatePrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Split.BuildingSplitController), "SendSplitRequest", nameof(BldSplitPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Split.BuildingSplitController), "ExecuteRequest", nameof(BldSplitExecPrefix));
            // Baustellen-Rohstoffe/Fortschritt vom Host (BuildResSync): beim Übernehmen keine Lebenspunkte nebenher,
            // beim Löschen einer Arbeit beim Gast keine übernommenen verbrauchten Rohstoffe fallen lassen
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Structure), "UpdateStructureHealthDuringBuilding", nameof(BuildResHealthPrefix));
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "UpdateStructureHealthDuringBuilding", nameof(BuildResHealthPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.Works.RepairStructureWork), "SetHealthToStructure", nameof(BuildResHealthPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.Works.ConstructableWork), "DeleteWork", nameof(BuildResDeletePrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.Works.ExtraAdaptationWork), "DeleteWork", nameof(BuildResDeletePrefix));
            TryPatch(harmony, typeof(Gameplay.Units.Workers.WorkSystem.Works.CustomizationWork), "DeleteWork", nameof(BuildResDeletePrefix));

            // Trupp-Anzeige (SquadViewSync): die stehende eigene Befehlskette des Gastes nicht zeichnen,
            // die Kette kommt vom Host (OrdersQueue.UpdatePath -> PathDrawer.UpdateNextPaths)
            TryPatchExact(harmony, typeof(Gameplay.Units.Movements.Paths.PathDrawer), "UpdateNextPaths",
                new[] { typeof(System.Collections.Generic.List<Gameplay.Units.Orders.OrderLine>) }, nameof(SvNextPathsPrefix), null);
            // Ereignis-Marker beim Gast nur vom Host, Ausloeser nur beim Host (markers, EventSync)
            EventSync.ApplyPatches(harmony);
            CrashGuard.ApplyPatches(harmony);
            // Haus des Mitspielers mit dem Original-Umriss des Spiels (markers, PresenceOutline)
            PresenceOutline.ApplyPatches(harmony);
            // Tages-Statistik der Rohstoffe (Tooltip der Rohstoffleiste) kommt vom Host (ResStatsSync): beim Gast
            // nimmt ein Postfix jede eigene Fortschreibung zurück (auch Tageswechsel, falls er unter NetApplying läuft)
            TryPatch(harmony, typeof(MainResourcesContainer), "ForceAddResource", nameof(ResStatsLockPrefix), nameof(ResStatsLockPostfix));
            TryPatch(harmony, typeof(MainResourcesContainer), "TryRemoveResource", nameof(ResStatsLockPrefix), nameof(ResStatsLockPostfix));
            TryPatch(harmony, typeof(MainResourcesContainer), "TimeOfDayChanged", nameof(ResStatsDayPrefix), nameof(ResStatsDayPostfix));

            // Trupp-Markierung „zuletzt gesteuert“ (SquadViewSync): Änderungen am Trupp über die Oberfläche melden
            // (Befehle meldet OrderSync). Nur melden, nie etwas verhindern.
            TryPatch(harmony, typeof(Group), "SetGroupName", nameof(SvGroupPrefix));
            TryPatch(harmony, typeof(StancesHandler), "SetStateToStance", nameof(SvStancePrefix));
            foreach (var m in new[] { "LeaveVehicleInvoke", "StartRepair", "PourFuelToTrunk", "ChangeFuelInState", "ChangeFuelOutState",
                                      "HandleSquadLockStateChanged", "HandleVehicleLockStateChanged" })
                TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), m, nameof(SvPanelPrefix));
            TryPatch(harmony, typeof(Gameplay.Units.SquadSplitPreparer), "PrepareSquadForSplit", nameof(SvSplitPrefix));
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.MembersDragStrategy), "PlaceMember", nameof(SvPlacePrefix));
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.MembersDragStrategy), "SwapMembers", nameof(SvSwapPrefix));
            foreach (var m in new[] { "PlaceResource", "SwapResource", "ApplyEffect" })
                TryPatch(harmony, typeof(Controllers.Dragging.Strategies.RegularDragStrategy), m, nameof(SvOpenPanelsPrefix));
            TryPatch(harmony, typeof(UI.VehicleAmmoContainerPanel), "HandleButtonClicked", nameof(SvOpenPanelsPrefix));
            TryPatch(harmony, typeof(Controllers.ResourceDiscardController), "HandleDiscardClicked", nameof(SvOpenPanelsPrefix));
            // Trupp-Name und Haltung des Gastes gehen an den Host (Paket K, SquadStateSync)
            TryPatch(harmony, typeof(Group), "SetGroupName", null, nameof(RenamePostfix));
            TryPatch(harmony, typeof(StancesHandler), "SetStateToStance", null, nameof(StancePostfix));
            // Tausch per Ziehen geht an den Host (Paket K, ExchangeSync)
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.RegularDragStrategy), "PlaceResource", nameof(DragPlacePrefix));
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.RegularDragStrategy), "SwapResource", nameof(DragSwapPrefix));
            TryPatch(harmony, typeof(Controllers.Dragging.Strategies.RegularDragStrategy), "ApplyEffect", nameof(DragEffectPrefix));
            // Bauaktionen des Gastes gehen an den Host (Paket H, BuildingSync)
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "StartAdapt", nameof(BuildAdaptPrefix));
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "StartExtraAdapt", nameof(BuildExtraPrefix));
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "StartDeadaptation", nameof(BuildDeadaptPrefix));
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "StartDemolish", nameof(BuildDemolishPrefix));
            TryPatch(harmony, typeof(Gameplay.Buildings.Building), "SetReadyToDemolish", nameof(BuildReadyPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Structure), "StartRepairing", nameof(BuildRepairPrefix));

            // Gruppen bauen: Gast nur auf Ansage des Hosts, Host meldet neue Gruppen
            TryPatch(harmony, typeof(GroupBuilder), "Build", nameof(BuildPrefix), nameof(BuildPostfix));

            // Kampf und Schaden: beim Gast nur noch Anzeige
            TryPatch(harmony, typeof(CharacterFightHandler), "Attack", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(CharacterFightHandler), "VirtualAttack", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(ProjectilesController), "LaunchProjectile", nameof(BlockWhenPuppet));
            // Explosionen bleiben beim Gast erlaubt: sie entstehen dort nur noch aus nachgespielten Geschossen
            // und richten keinen Schaden an (ReceiveDamage ist gesperrt).
            TryPatch(harmony, typeof(Character), "ReceiveDamage", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(VirtualCharacter), "ReceiveDamage", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Vehicle), "ReceiveDamage", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(Structure), "ReceiveDamage", nameof(BlockWhenPuppet));

            // Stufe 2: Kampf-Optik vom Host
            TryPatchExact(harmony, typeof(CharacterFightHandler), "Attack",
                new[] { typeof(IAttackable), typeof(Vector3), typeof(bool), typeof(bool), typeof(bool) },
                null, nameof(AttackPostfix));
            TryPatchExact(harmony, typeof(ProjectilesController), "LaunchProjectile",
                new[] { typeof(ProjectileDraft), typeof(Vector3), typeof(Vector3), typeof(IAreaDamageApplier) },
                null, nameof(ProjectilePostfix));

            // Stufe 2: Trupps und eigene Leute
            TryPatch(harmony, typeof(SquadCreatorController), "AddCreationRequestAtStructure", nameof(CreationRequestPrefix));
            TryPatch(harmony, typeof(SquadCreatorController), "AddReplenishRequestAtStructure", nameof(ReplenishRequestPrefix));
            TryPatch(harmony, typeof(SquadsCreator), "CreateSquadFromRequest", nameof(LocalSquadCreationPrefix));
            TryPatch(harmony, typeof(SquadCreatorController), "HandleRequestDestroyed", nameof(RequestDestroyedPrefix));
            // Statistik je Spieler (StatsSync): Trupp-Ausbildung, Treffer/Tod mit Angreifer
            TryPatch(harmony, typeof(SquadCreatorController), "AddCreationRequestAtStructure", nameof(StatsSquadPrefix));
            TryPatch(harmony, typeof(Character), "ReceiveDamage", nameof(StatsDamagePrefix), nameof(StatsDamagePostfix));
            // „Plündern: x %“ im Trupp-Fenster: das Fenster meldet sich bei jedem Neuaufbau zusätzlich an der Plünder-Arbeit
            // an und nie ab (Spielfehler, im Koop öfter ausgelöst) → Zahlen anderer Häuser/Trupps springen hinein.
            // Immer den Fortschritt des Hauses zeigen, in dem der angezeigte Trupp gerade ist.
            TryPatch(harmony, typeof(UI.InfoPanels.SquadPanelInfo), "UpdateScavengeProgress", nameof(ScavProgressPrefix));
            // Lager-Fenster: ein Rohstoff ohne Spieldaten ließ RefreshUI abstürzen, Tauschen mit dem Lager hing dann
            // (Bericht 2026-11-07). Fehler abfangen, damit der Tausch weiterläuft.
            try
            {
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(UI.InfoPanels.StoragePanel)).Where(x => x.Name == "RefreshUI"))
                {
                    harmony.Patch(m, finalizer: new HarmonyMethod(typeof(UnitPatches), nameof(StorageRefreshFinalizer)));
                    Applied++;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("IFZCOOP StoragePanel-Schutz nicht gesetzt: " + e.Message); }
            // Entwickler-Menü: unsterbliche Trupps (CheatWindow.Immortal) bekommen keinen Lebenspunkt abgezogen
            TryPatch(harmony, typeof(Character), "SubtractHp", nameof(ImmortalPrefix));
            // Entwickler-Menü: unzerstörbare Fahrzeuge (CheatWindow.Indestructible), nur das gewählte Fahrzeug
            TryPatch(harmony, typeof(Vehicle), "SubtractHp", nameof(VehicleHpPrefix));
            // Entwickler-Menü: unzerstörbare Gebäude (CheatWindow.IndestructibleBuildings) – am Health selbst,
            // weil Gebäude an mehreren Stellen direkt Health.SubtractHp aufrufen
            TryPatch(harmony, typeof(Gameplay.Units.Health), "SubtractHp", nameof(HealthHpPrefix));
            // Entwickler-Menü: schneller plündern je Trupp (CheatWindow.ScavBoost), Sofort töten (OneHit/OneHitAll)
            TryPatch(harmony, typeof(ScavengeWork), "Execute", nameof(ScavBoostPrefix));
            TryPatch(harmony, typeof(Vehicle), "UpdateMaxSpeed", null, nameof(VehicleSpeedPostfix));
            // Mod-Fenster (F7, Statistik, Farbrad, F8): Maus darüber zählt als „über der Oberfläche“ → keine Klicks,
            // kein Kartenziehen, kein Zoomen auf der Karte darunter (Bericht 2026-11-07)
            TryPatch(harmony, typeof(GameInput.GameCursor), "IsPointerOverUIIgnoringLayer", null, nameof(OverModUiPostfix));
            TryPatch(harmony, typeof(GameInput.GameCursor), "get_IsCursorOverGameObject", null, nameof(OverModUiPostfix));
            TryPatch(harmony, typeof(Cameras.GameCameraInput), "GetZoomDelta", null, nameof(ZoomOverModUiPostfix));
            TryPatch(harmony, typeof(Gameplay.Units.Movements.Movement), "CalculateSpeed", null, nameof(WalkPostfix));
            try
            {
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(Character)).Where(x => x.Name == "ReceiveDamage"))
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(UnitPatches), nameof(OneHitPrefix)) { priority = Priority.Last });
                    Applied++;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("IFZCOOP Sofort-töten-Haken nicht gesetzt: " + e.Message); }
            TryPatch(harmony, typeof(SquadsCreator), "ReplenishGroup", nameof(LocalSquadCreationPrefix));
            // Prefix, nicht Postfix: danach ist die Figur schon aus ihrem Trupp und gilt weder als Soldat noch Arbeiter.
            TryPatch(harmony, typeof(Character), "DestroyCharacter", nameof(DestroyCharacterPrefix));

            // Lager: Essen und Verderb rechnet der Host (StockSync)
            TryPatch(harmony, typeof(FoodConsumeController), "HandleHourChange", nameof(BlockWhenPuppet));
            TryPatch(harmony, typeof(MainResourcesContainer), "TimeOfDayChanged", nameof(BlockWhenPuppet));
            // Signal „Rohstoff ins Lager“ (nur Missionsziele hören zu) und Wissenschaftsmaterial: beim Gast nie (EventSync/WorldSync)
            TryPatch(harmony, typeof(StockroomsController), "OnResourceAdded", nameof(BlockAlwaysWhenPuppet));

            // Behälter und Ausrüstung kommen vom Host (ContainerSync): beim Gast nichts selbst finden, abgeben,
            // auffüllen oder fallen lassen. Auch nicht beim Nachspielen eines Todes (NetApplying) – sonst lägen
            // Kisten mit Gast-IDs herum.
            TryPatch(harmony, typeof(ScavengeWork), "DiscoveredResourceCrate", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(ScavengeWork), "DiscoverAllResources", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Group), "ReturnGroupResourcesToStockroom", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Group), "RefillResourcesFromStockroom", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Group), "DropResource", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(Group), "DropAsResource", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(ItemsContainer), "TryToDropCrate", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(ItemsContainer), "HandleCharacterDestroy", nameof(BlockAlwaysWhenPuppet));
            TryPatch(harmony, typeof(ItemsContainer), "RefillItems", nameof(BlockAlwaysWhenPuppet));
            // Bodenkisten (auch Fleisch beim Tod) legt nur der Host ab, der Gast bekommt sie über GroundSync.
            TryPatch(harmony, typeof(ResourcesController), "DropResourceToCustomPos", nameof(BlockAlwaysWhenPuppet));

            // Hauptquartier wählt nur der Host
            TryPatch(harmony, typeof(HqSetterController), "OnSelectionConfirmed", nameof(HqConfirmPrefix));
            TryPatch(harmony, typeof(Gameplay.Rebuilding.Towers.ConvoyStructureController), "SetupHq", nameof(SetupHqPrefix), nameof(SetupHqPostfix));

            Plugin.Log.LogInfo($"Einheiten-Abgleich: {Applied} Haken gesetzt, {Missing} fehlen");
        }

        private static void TryPatchExact(Harmony harmony, Type type, string method, Type[] args, string prefix, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method, args);
                if (target == null)
                {
                    Missing++;
                    Plugin.Log.LogWarning($"IFZCOOP unit hook fehlt: {type.Name}.{method}");
                    return;
                }
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(UnitPatches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(UnitPatches), postfix));
                Applied++;
            }
            catch (Exception e)
            {
                Missing++;
                Plugin.Log.LogWarning($"IFZCOOP unit hook {type.Name}.{method} nicht gesetzt: {e.Message}");
            }
        }

        private static Session CurrentSession => Plugin.Instance?.Session;

        // CharacterFightHandler.Attack(IAttackable opponent, Vector3 attackPoint, bool, bool, bool)
        private static void AttackPostfix(CharacterFightHandler __instance, IAttackable opponent, Vector3 attackPoint, bool __result)
        {
            if (!__result)
                return;
            try { CurrentSession?.Combat?.OnAttack(__instance, opponent, attackPoint); }
            catch (Exception e) { Plugin.Log.LogDebug("Schuss melden: " + e.Message); }
        }

        // ProjectilesController.LaunchProjectile(ProjectileDraft projectileDraft, Vector3 startPosition, Vector3 endPosition, IAreaDamageApplier areaDamageApplier)
        private static void ProjectilePostfix(ProjectileDraft projectileDraft, Vector3 startPosition, Vector3 endPosition, IAreaDamageApplier areaDamageApplier, bool __runOriginal)
        {
            if (!__runOriginal)
                return;
            try { CurrentSession?.Combat?.OnProjectileLaunched(projectileDraft, startPosition, endPosition, areaDamageApplier); }
            catch (Exception e) { Plugin.Log.LogDebug("Geschoss melden: " + e.Message); }
        }

        // SquadCreatorController.AddCreationRequestAtStructure(List<StaticSkillInfo> staticSkillInfos, ResourceID selectedWeapon, ResourceID selectedGear, Structure structure, bool duringTraining)
        private static bool CreationRequestPrefix(System.Collections.Generic.List<StaticSkillInfo> staticSkillInfos, ResourceID selectedWeapon, ResourceID selectedGear, Structure structure, bool duringTraining)
        {
            var squads = CurrentSession?.Squads;
            return squads == null || squads.ForwardCreation(staticSkillInfos, selectedWeapon, selectedGear, structure, duringTraining);
        }

        // SquadCreatorController.AddReplenishRequestAtStructure(Group squad, Structure structure)
        private static bool ReplenishRequestPrefix(Group squad, Structure structure)
        {
            var squads = CurrentSession?.Squads;
            return squads == null || squads.ForwardReplenish(squad, structure);
        }

        private static void StatsSquadPrefix()
        {
            var s = CurrentSession;
            if (s != null && s.Role != Role.Guest && !UnitSync.NetApplying)
                s.Stats?.AddForActor(Sync.StatsSync.Stat.Squads);
        }

        private static bool HealthHpPrefix(Gameplay.Units.Health __instance)
        {
            return CheatWindow.IndestructibleBuildings.Count == 0 || !CheatWindow.IsIndestructible(__instance);
        }

        private static void OverModUiPostfix(ref bool __result)
        {
            if (!__result && Menu.ModUi.MouseOverWindow()) __result = true;
        }

        private static void ZoomOverModUiPostfix(ref float __result)
        {
            if (__result != 0f && Menu.ModUi.MouseOverWindow()) __result = 0f;
        }

        // Movement.CalculateSpeed(...) → Weg pro Bild; Lauftempo je Trupp (CheatWindow.WalkByMovement)
        private static void WalkPostfix(Gameplay.Units.Movements.Movement __instance, ref float __result)
        {
            if (CheatWindow.WalkByMovement.Count == 0 || __instance == null) return;
            if (CheatWindow.WalkByMovement.TryGetValue(__instance, out float f)) __result *= f;
        }

        // Vehicle.UpdateMaxSpeed(): Tempo je Fahrzeug (CheatWindow.SpeedBoost, 0.5 bis 5)
        private static void VehicleSpeedPostfix(Vehicle __instance)
        {
            if (CheatWindow.SpeedBoost.Count == 0 || __instance == null || string.IsNullOrEmpty(__instance.Id)) return;
            if (CheatWindow.SpeedBoost.TryGetValue(__instance.Id, out float f) && Mathf.Abs(f - 1f) > 0.01f && __instance.Movement != null)
                __instance.Movement.MaxSpeed *= f;
        }

        // ScavengeWork.Execute(Character character, float timeToAdd)
        private static void ScavBoostPrefix(Character character, ref float timeToAdd)
        {
            if (CheatWindow.ScavBoost.Count == 0 || character == null) return;
            var g = character.Group;
            if (g != null && !string.IsNullOrEmpty(g.Id) && CheatWindow.ScavBoost.TryGetValue(g.Id, out float f) && f > 1f)
                timeToAdd *= f;
        }

        // Character.ReceiveDamage(Weapon attackedWith, float dmg, SubtractionHpReason reason, IDamageApplier damageApplier)
        // Sofort töten: Gegner eines markierten Spieler-Trupps verliert beim Treffer alles (auch durch Schutzkleidung).
        private static bool OneHitPrefix(Character __instance, Gameplay.Units.Equipment.Weapon attackedWith, Gameplay.Units.SubtractionHpReason reason,
            Gameplay.Units.Interfaces.IDamageApplier damageApplier, ref bool __result)
        {
            if (!CheatWindow.OneHitAll && CheatWindow.OneHit.Count == 0) return true;
            try
            {
                if (__instance == null || __instance.IsDead()) return true;
                if (__instance.Group != null && __instance.Group.Fraction == Fraction.Player) return true; // nur Gegner
                var g = Sync.StatsSync.AttackerGroup(damageApplier);
                if (g == null || g.Fraction != Fraction.Player) return true;
                if (!CheatWindow.OneHitAll && (string.IsNullOrEmpty(g.Id) || !CheatWindow.OneHit.Contains(g.Id))) return true;
                __instance.SubtractHp(1000000f, reason, attackedWith);
                __result = true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        // Vehicle.SubtractHp(float amount, SubtractionHpReason deathReason, Weapon weapon)
        private static bool VehicleHpPrefix(Vehicle __instance)
        {
            return CheatWindow.Indestructible.Count == 0 || __instance == null || string.IsNullOrEmpty(__instance.Id)
                   || !CheatWindow.Indestructible.Contains(__instance.Id);
        }

        public static int StorageErrorsCaught;

        private static Exception StorageRefreshFinalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (StorageErrorsCaught++ < 3)
                Plugin.Log.LogWarning("IFZCOOP Lager-Fenster: Fehler abgefangen (Tauschen läuft weiter): " + __exception.Message);
            return null;
        }

        public static int ScavProgressFixed;

        // SquadPanelInfo.UpdateScavengeProgress(float percentages)
        private static bool ScavProgressPrefix(UI.InfoPanels.SquadPanelInfo __instance, ref float percentages)
        {
            try
            {
                var g = __instance != null ? __instance.Squad : null;
                var s = g != null ? g.GetOccupiedScavengable() : null;
                if (g == null || s == null || g.PlayerGroupStatusHandler == null
                    || g.PlayerGroupStatusHandler.Status != Gameplay.Units.Player.PlayerGroupStatus.Scavenging)
                    return false; // Meldung von einem fremden Haus: nicht anzeigen
                float mine = s.GetScavengeProgress();
                if (Mathf.Abs(mine - percentages) > 0.0005f) ScavProgressFixed++;
                percentages = mine;
                return true;
            }
            catch
            {
                return true;
            }
        }

        // Character.SubtractHp(float amount, SubtractionHpReason deathReason, Weapon weapon)
        private static bool ImmortalPrefix(Character __instance)
        {
            if (CheatWindow.Immortal.Count == 0 || __instance == null) return true;
            var g = __instance.Group;
            return g == null || string.IsNullOrEmpty(g.Id) || !CheatWindow.Immortal.Contains(g.Id);
        }

        // Character.ReceiveDamage(Weapon attackedWith, float dmg, SubtractionHpReason reason, IDamageApplier damageApplier)
        private static void StatsDamagePrefix(Character __instance, out bool __state)
        {
            __state = __instance != null && !__instance.IsDead();
        }

        private static void StatsDamagePostfix(Character __instance, float dmg, Gameplay.Units.Interfaces.IDamageApplier damageApplier, bool __result, bool __state)
        {
            var s = CurrentSession;
            if (s == null || s.Role == Role.Guest) return;
            try { s.Stats?.OnDamage(__instance, dmg, damageApplier, __result, __state); }
            catch (Exception e) { Plugin.Log.LogDebug("Statistik Treffer: " + e.Message); }
        }

        // SquadCreatorController.HandleRequestDestroyed(SquadCreationRequest, Structure): beim Gast an den Host schicken
        private static bool RequestDestroyedPrefix(SquadCreationRequest selectedRequest, Structure structure)
        {
            var q = CurrentSession?.SquadQueue;
            return q == null || q.ForwardCancel(selectedRequest, structure);
        }

        // SquadsCreator.CreateSquadFromRequest / ReplenishGroup: Trupps entstehen beim Gast nur auf Ansage des Hosts.
        private static bool LocalSquadCreationPrefix()
        {
            var squads = CurrentSession?.Squads;
            return squads == null || squads.AllowLocalSquadCreation();
        }

        // Character.DestroyCharacter(bool silent) – meldet nur, verhindert nie etwas
        private static void DestroyCharacterPrefix(Character __instance, bool silent)
        {
            try { CurrentSession?.Squads?.OnCharacterDestroyed(__instance, silent); }
            catch (Exception e) { Plugin.Log.LogDebug("Tod melden: " + e.Message); }
        }

        // HqSetterController.OnSelectionConfirmed(): beim Gast verwerfen
        private static bool HqConfirmPrefix(HqSetterController __instance)
        {
            var hq = CurrentSession?.Hq;
            return hq == null || hq.GuestConfirmPrefix(__instance);
        }

        // ConvoyStructureController.SetupHq(Group squad): beim Gast an den Host, beim Host danach Gäste neu laden
        private static bool SetupHqPrefix(Group squad)
        {
            var hq = CurrentSession?.Hq;
            return hq == null || hq.ForwardSetupHq(squad);
        }

        private static void SetupHqPostfix(bool __runOriginal)
        {
            if (__runOriginal)
                CurrentSession?.Hq?.OnSetupHq();
        }

        /// <summary>Patcht alle in der Klasse selbst deklarierten Überladungen mit diesem Namen.</summary>
        private static void TryPatch(Harmony harmony, Type type, string method, string prefix, string postfix = null)
        {
            try
            {
                var targets = AccessTools.GetDeclaredMethods(type)
                    .Where(m => m.Name == method && !m.IsAbstract && !m.ContainsGenericParameters)
                    .ToList();
                if (targets.Count == 0)
                {
                    Missing++;
                    Plugin.Log.LogWarning($"IFZCOOP unit hook fehlt: {type.Name}.{method}");
                    return;
                }
                foreach (var target in targets)
                {
                    harmony.Patch(target,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(UnitPatches), prefix),
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(UnitPatches), postfix));
                    Applied++;
                }
            }
            catch (Exception e)
            {
                Missing++;
                Plugin.Log.LogWarning($"IFZCOOP unit hook {type.Name}.{method} nicht gesetzt: {e.Message}");
            }
        }

        private static WorldSync World => Plugin.Instance?.Session?.World;

        private static bool ResearchStartPrefix(Gameplay.Research.ResearchID researchId)
            => World == null || World.GuestResearchClick(researchId, cancel: false);

        private static bool ResearchCancelPrefix(Gameplay.Research.ResearchID researchId)
            => World == null || World.GuestResearchClick(researchId, cancel: true);

        private static bool LawSetPrefix(Gameplay.Laws.Law __instance, bool isEnabled)
            => World == null || World.GuestLawSet(__instance, isEnabled);

        private static BuildingSync Buildings => Plugin.Instance?.Session?.Buildings;

        private static bool BuildAdaptPrefix(Gameplay.Buildings.Building __instance, global::PlaceableObjectDraft placeableObjectDraft)
            => Buildings == null || Buildings.GuestAction(__instance, BuildingSync.Action.Adapt, placeableObjectDraft?.Id, __instance.PartialBuildingHandler != null ? __instance.PartialBuildingHandler.TargetValue : 1f);

        private static bool BuildExtraPrefix(Gameplay.Buildings.Building __instance)
            => Buildings == null || Buildings.GuestAction(__instance, BuildingSync.Action.ExtraAdapt, null, __instance.PartialBuildingHandler != null ? __instance.PartialBuildingHandler.TargetValue : 1f);

        private static bool BuildDeadaptPrefix(Gameplay.Buildings.Building __instance)
            => Buildings == null || Buildings.GuestAction(__instance, BuildingSync.Action.Deadapt, null, 0f);

        private static bool BuildDemolishPrefix(Gameplay.Buildings.Building __instance)
            => Buildings == null || Buildings.GuestAction(__instance, BuildingSync.Action.Demolish, null, 0f);

        private static bool BuildReadyPrefix(Gameplay.Buildings.Building __instance, bool isReady)
            => Buildings == null || Buildings.GuestAction(__instance, BuildingSync.Action.Ready, null, isReady ? 1f : 0f);

        private static bool BuildRepairPrefix(Gameplay.Rebuilding.Structure __instance)
            => Buildings == null || Buildings.GuestAction(__instance, BuildingSync.Action.Repair, null, 0f);

        private static ExchangeSync Exchange => Plugin.Instance?.Session?.Exchange;

        private static bool DragPlacePrefix(Controllers.Dragging.Strategies.RegularDragStrategy __instance, Gameplay.GameResources.ResourceID resourceId, float quantity)
            => Exchange == null || Exchange.GuestDrag(__instance, ExchangeSync.Op.Place, resourceId, quantity, default, 0f, false);

        private static bool DragSwapPrefix(Controllers.Dragging.Strategies.RegularDragStrategy __instance, Gameplay.GameResources.ResourceID firstResourceId, float firstQuantity, Gameplay.GameResources.ResourceID secondResourceId, float secondQuantity)
            => Exchange == null || Exchange.GuestDrag(__instance, ExchangeSync.Op.Swap, firstResourceId, firstQuantity, secondResourceId, secondQuantity, false);

        private static bool DragEffectPrefix(Controllers.Dragging.Strategies.RegularDragStrategy __instance, Gameplay.GameResources.ResourceID resourceId, float quantity, bool fromLockedResource)
            => Exchange == null || Exchange.GuestDrag(__instance, ExchangeSync.Op.Effect, resourceId, quantity, default, 0f, fromLockedResource);

        private static void RenamePostfix(Group __instance, string n) => Plugin.Instance?.Session?.SquadStates?.GuestRenamed(__instance, n);

        private static void StancePostfix(StancesHandler __instance, GroupStanceId groupStanceId, bool isActive)
            => Plugin.Instance?.Session?.SquadStates?.GuestStance(__instance, groupStanceId, isActive);

        private static WorkSync Works => Plugin.Instance?.Session?.Works;
        private static bool Wui(UI.InfoPanels.WorkersCountChanger ui, WorkSync.Op op, int p = 0, bool left = true) => Works == null || Works.GuestWorkUi(ui, op, p, left);
        private static bool Lui(global::WorkersLabel l, WorkSync.Op op, int p = 0, bool left = true) => Works == null || Works.GuestLabelUi(l, op, p, left);
        private static bool WcAdd(UI.InfoPanels.WorkersCountChanger __instance) => Wui(__instance, WorkSync.Op.Add);
        private static bool WcRemove(UI.InfoPanels.WorkersCountChanger __instance) => Wui(__instance, WorkSync.Op.Remove);
        private static bool WcAddMax(UI.InfoPanels.WorkersCountChanger __instance) => Wui(__instance, WorkSync.Op.AddMax);
        private static bool WcRemoveAll(UI.InfoPanels.WorkersCountChanger __instance) => Wui(__instance, WorkSync.Op.RemoveAll);
        private static bool WcIncMax(UI.InfoPanels.WorkersCountChanger __instance) => Wui(__instance, WorkSync.Op.IncMax);
        private static bool WcDecMax(UI.InfoPanels.WorkersCountChanger __instance) => Wui(__instance, WorkSync.Op.DecMax);
        private static bool WcPriority(UI.InfoPanels.WorkersCountChanger __instance, int priority, bool leftButton) => Wui(__instance, WorkSync.Op.Priority, priority, leftButton);
        private static bool LbZero(global::WorkersLabel __instance) => Lui(__instance, WorkSync.Op.LZero);
        private static bool LbMax(global::WorkersLabel __instance) => Lui(__instance, WorkSync.Op.LMax);
        private static bool LbAdd(global::WorkersLabel __instance) => Lui(__instance, WorkSync.Op.LAdd);
        private static bool LbRemove(global::WorkersLabel __instance) => Lui(__instance, WorkSync.Op.LRemove);
        private static bool LbAddMax(global::WorkersLabel __instance) => Lui(__instance, WorkSync.Op.LAddMax);
        private static bool LbRemoveAll(global::WorkersLabel __instance) => Lui(__instance, WorkSync.Op.LRemoveAll);
        private static bool LbPriority(global::WorkersLabel __instance, int priority, bool leftButton) => Lui(__instance, WorkSync.Op.LPriority, priority, leftButton);

        private static bool AlarmStartPrefix(ref bool __result) { if (Works == null || Works.GuestAlarm(true)) return Alarm(ref __result); __result = true; return false; }
        private static bool AlarmStopPrefix(ref bool __result) { if (Works == null || Works.GuestAlarm(false)) return Alarm(ref __result); __result = true; return false; }
        // Beim Gast nie selbst (Stand kommt vom Host), sonst normal
        private static bool Alarm(ref bool __result) { if (!Puppet) return true; __result = true; return false; }

        private static TraderSync Traders => Plugin.Instance?.Session?.Traders;
        private static bool TradeAcceptPrefix() => Traders == null || Traders.GuestButton(TraderSync.Op.Accept);
        private static bool TradeCancelPrefix() => Traders == null || Traders.GuestButton(TraderSync.Op.Cancel);
        private static bool TradeThreatenPrefix() => Traders == null || Traders.GuestButton(TraderSync.Op.Threaten);
        private static bool TradeDragPlacePrefix(Controllers.Dragging.Strategies.RegularDragStrategy __instance, Gameplay.GameResources.ResourceID resourceId, float quantity)
            => Traders == null || Traders.GuestDrag(__instance, false, resourceId, quantity, default, 0f);
        private static bool TradeDragSwapPrefix(Controllers.Dragging.Strategies.RegularDragStrategy __instance, Gameplay.GameResources.ResourceID firstResourceId, float firstQuantity, Gameplay.GameResources.ResourceID secondResourceId, float secondQuantity)
            => Traders == null || Traders.GuestDrag(__instance, true, firstResourceId, firstQuantity, secondResourceId, secondQuantity);

        // Character.CustomUpdate(): beim Gast für Bürger mit Host-Daten nur der Marionetten-Takt (WorkerPosSync)
        private static bool WorkerPosUpdatePrefix(Character __instance)
        {
            var wp = WorkerPosSync.Active;
            return wp == null || !wp.PuppetTick(__instance);
        }

        // ResourceInHandModule.GetResourceFrom/ReturnResourceTo/ReturnResourceToStructure: das Lager rechnet der Host
        private static bool WorkerStockPrefix()
        {
            if (!Puppet)
                return true;
            WorkerPosSync.StockBlocked++;
            return false;
        }

        // Character.DestroyCharacter(bool silent): Kiste in der Hand des Gastes nicht ins Lager/zu Boden – meldet nur
        private static void WorkerPosDestroyPrefix(Character __instance)
        {
            try { Plugin.Instance?.Session?.WorkerPos?.OnCharacterDestroyed(__instance); }
            catch (Exception e) { Plugin.Log.LogDebug("Arbeiter-Tod: " + e.Message); }
        }

        private static AreaWorkSync AreaWorkS => Plugin.Instance?.Session?.AreaWorks;
        // AreaWorkController.CreateAreaWork(AreaWorkType areaWorkType, List<AggregatableWork> works, int, bool)
        private static bool AreaCreatePrefix(Gameplay.Units.Player.Workers.WorkSystem.AreaWorkSystem.AreaWorkType areaWorkType,
            System.Collections.Generic.List<Gameplay.Units.Workers.AggregatableWork> works, ref Gameplay.Units.Workers.AreaWork __result)
        {
            var s = AreaWorkS;
            if (s == null || s.GuestCreate(areaWorkType, works)) return true;
            __result = null;
            return false;
        }
        // AggregateWork.DeleteWork() – nur AreaWork betroffen
        private static bool AreaDeletePrefix(Gameplay.Units.Workers.AggregateWork __instance) => AreaWorkS == null || AreaWorkS.GuestDelete(__instance);
        // GatherResourcesWork.Execute / PlantTreeWork.Execute: beim Gast kein eigener Fortschritt
        private static bool AreaExecutePrefix(Gameplay.Units.Player.Workers.WorkSystem.WorkBase __instance, ref Gameplay.Units.Player.Workers.WorkSystem.WorkStatus __result)
        {
            if (!Puppet || UnitSync.NetApplying) return true;
            __result = __instance.IsDone ? Gameplay.Units.Player.Workers.WorkSystem.WorkStatus.Completed : Gameplay.Units.Player.Workers.WorkSystem.WorkStatus.InProgress;
            return false;
        }
        // SaveHandler.SaveGame(Map, string directoryName, string saveName, bool autoSave) – async, __result ist die Task
        private static void SaveGamePostfix(string directoryName, System.Threading.Tasks.Task<bool> __result)
        {
            try { Plugin.Instance?.Session?.HostOnSaveStarted(directoryName, __result); }
            catch (Exception e) { Plugin.Log.LogDebug("Speichern-Hinweis: " + e.Message); }
            try { SaveExtras.OnSave(directoryName, __result); }
            catch (Exception e) { Plugin.Log.LogDebug("Spielstand-Zusatz: " + e.Message); }
        }
        // AreaWork.UpdateProfit(): beim Gast Restmenge des Hosts (Anzeige „Gesammelte Ressourcen“)
        private static void AreaProfitPostfix(Gameplay.Units.Workers.AreaWork __instance)
        {
            if (Plugin.Instance?.Session?.Role != Role.Guest) return;
            AreaWorkSync.GuestProfitPostfix(__instance);
        }
        // GatherableObjectContainer.RemoveGatherable(GatherableObject) – über das Ereignis OnDestroyedGatherable
        private static void GatherableGonePostfix(Gameplay.ObjectOnMap.GatherableObject gatherableObject)
        {
            try { AreaWorkS?.HostOnGone(gatherableObject); }
            catch (Exception e) { Plugin.Log.LogDebug("Sammelobjekt weg: " + e.Message); }
        }

        private static ProductionSync Production => Plugin.Instance?.Session?.Production;
        private static bool ProdBarPrefix(UI.InfoPanels.NestedUIElements.ProductionPanel __instance, UI.InfoPanels.NestedUIElements.ProductionBar newProduction, bool allProductions)
            => Production == null || Production.GuestProductionBar(__instance, newProduction, allProductions);
        private static bool ShopBuildPrefix(Gameplay.Buildings.Building building, Gameplay.Buildings.CarWorkshopRequest request)
            => Production == null || Production.GuestShopBuild(building, request);
        private static bool ShopModifyPrefix(Gameplay.Buildings.Building building, Gameplay.Vehicles.Vehicle vehicleToModify, Gameplay.Buildings.CarWorkshopRequest request)
            => Production == null || Production.GuestShopVehicle(building, vehicleToModify, request, false);
        private static bool ShopScrapPrefix(Gameplay.Buildings.Building building, Gameplay.Vehicles.Vehicle vehicleToDeconstruct)
            => Production == null || Production.GuestShopVehicle(building, vehicleToDeconstruct, null, true);
        private static bool ShopCancelPrefix(Gameplay.Buildings.Building building, Gameplay.Buildings.CarWorkshopRequest request)
            => Production == null || Production.GuestCancelRequest(building, request);
        private static bool ShopAddPrefix(Gameplay.Buildings.CarWorkshopHandler __instance, Gameplay.Buildings.CarWorkshopRequest request, bool isAddManually)
            => Production == null || Production.GuestAddRequest(__instance, request, isAddManually);
        private static bool ShopSelectPrefix(Gameplay.Buildings.CarWorkshopHandler __instance, Gameplay.Buildings.CarWorkshopRequest request)
            => Production == null || Production.GuestSelectRequest(__instance, request);
        private static bool SawPrefix(Gameplay.Units.Workers.SawmillWork __instance, bool newState)
            => Production == null || Production.GuestSawmill(__instance, newState);
        private static bool HandyPrefix(Gameplay.Units.Workers.HandymanWork __instance, Gameplay.Buildings.BuildingSection sectionId, int newPriority)
            => Production == null || Production.GuestHandyman(__instance, sectionId, newPriority);
        private static bool CustomizePrefix(Controllers.CustomizeBuildingController __instance)
            => Production == null || Production.GuestCustomizeConfirm(__instance);
        private static bool CustomEnterPrefix(object __instance)
        {
            Production?.GuestCustomizationEnter(__instance);
            return true;
        }

        private static StructureSync Structs => Plugin.Instance?.Session?.Structures;
        // X.OnCursorReleased(bool placeObject): beim Gast als Wunsch an den Host, die Vorschau verwirft das Spiel selbst
        private static bool StructPlacePrefix(Gameplay.Rebuilding.PlaceableObject __instance, ref bool placeObject)
        {
            if (placeObject && Structs != null && Structs.GuestPlace(__instance)) placeObject = false;
            return true;
        }
        // WallConstructor.End(bool build, bool immediately)
        private static bool StructWallEndPrefix(Gameplay.Rebuilding.Walls.WallConstructor __instance, ref bool build, bool immediately)
        {
            if (build && Structs != null && Structs.GuestWallEnd(__instance, immediately)) build = false;
            return true;
        }
        private static bool StructDeconstructPrefix(Gameplay.Rebuilding.Structure __instance) => Structs == null || Structs.GuestStructAction(__instance, StructureSync.Op.Deconstruct);
        private static bool StructRepairPrefix(Gameplay.Rebuilding.Structure __instance) => Structs == null || Structs.GuestStructAction(__instance, StructureSync.Op.Repair);
        private static bool StructPanelCancelPrefix(UI.InfoPanels.SpecificPanelInfo __instance) => Structs == null || Structs.GuestPanelCancel(__instance);
        private static bool StructPanelActionPrefix(UI.InfoPanels.SpecificPanelInfo __instance, UI.InfoPanels.NestedUIElements.InfoPanelAction infoPanelAction)
            => infoPanelAction != UI.InfoPanels.NestedUIElements.InfoPanelAction.CancelInfoPanelButton || Structs == null || Structs.GuestPanelCancel(__instance);
        private static bool StructDonePrefix(Gameplay.Rebuilding.Structure __instance) => Structs == null || Structs.AllowConstructionDone(__instance);

        private static SquadActionSync SquadActs => Plugin.Instance?.Session?.SquadActions;
        private static bool SaLeavePrefix(UI.InfoPanels.SquadPanelInfo __instance) => SquadActs == null || SquadActs.GuestLeave(__instance);
        private static bool SaRepairPrefix(UI.InfoPanels.SquadPanelInfo __instance) => SquadActs == null || SquadActs.GuestSquadRepair(__instance);
        private static bool SaPourPrefix(UI.InfoPanels.SquadPanelInfo __instance) => SquadActs == null || SquadActs.GuestPourFuel(__instance);
        private static bool SaFuelInPrefix(UI.InfoPanels.SquadPanelInfo __instance) => SquadActs == null || SquadActs.GuestFuel(__instance, true);
        private static bool SaFuelOutPrefix(UI.InfoPanels.SquadPanelInfo __instance) => SquadActs == null || SquadActs.GuestFuel(__instance, false);
        private static bool SaLockSquadPrefix(UI.InfoPanels.SquadPanelInfo __instance, bool isLocked) => SquadActs == null || SquadActs.GuestLock(__instance, false, isLocked);
        private static bool SaLockVehiclePrefix(UI.InfoPanels.SquadPanelInfo __instance, bool isLocked) => SquadActs == null || SquadActs.GuestLock(__instance, true, isLocked);
        private static bool SaAbRepairPrefix(UI.InfoPanels.AbandonedVehiclePanelInfo __instance) => SquadActs == null || SquadActs.GuestAbandonedRepair(__instance);
        private static bool SaCancelRepairPrefix(UI.InfoPanels.VehicleInRepairPanel __instance) => SquadActs == null || SquadActs.GuestCancelRepair(__instance);
        private static bool SaRepairActionPrefix(UI.InfoPanels.VehicleInRepairPanel __instance, UI.InfoPanels.NestedUIElements.InfoPanelAction infoPanelAction)
            => SquadActs == null || SquadActs.GuestRepairPanelAction(__instance, infoPanelAction);
        private static bool SaAmmoPrefix(UI.VehicleAmmoContainerPanel __instance) => SquadActs == null || SquadActs.GuestAmmoRefill(__instance);
        private static bool SaSplitPrefix(Group mainSquad) => SquadActs == null || SquadActs.GuestSplit(mainSquad);
        // MembersDragStrategy.PlaceMember(Character firstCharacter, Group secondGroup, int index)
        private static bool SaPlacePrefix(Character firstCharacter, Group secondGroup, int index) => SquadActs == null || SquadActs.GuestPlace(firstCharacter, secondGroup, index);
        private static void SaPlacePostfix(Character firstCharacter, Group secondGroup, int index, bool __runOriginal)
        {
            if (__runOriginal) SquadActs?.HostPlaced(firstCharacter, secondGroup, index);
        }
        // MembersDragStrategy.SwapMembers(Character firstCharacter, Character secondCharacter, int firstMemberStartIndex, int secondMemberStartIndex)
        private static bool SaSwapPrefix(Character firstCharacter, Character secondCharacter, int firstMemberStartIndex, int secondMemberStartIndex, out string[] __state)
        {
            __state = new[] { firstCharacter != null && firstCharacter.Group != null ? firstCharacter.Group.Id : null,
                              secondCharacter != null && secondCharacter.Group != null ? secondCharacter.Group.Id : null };
            return SquadActs == null || SquadActs.GuestSwap(firstCharacter, secondCharacter, firstMemberStartIndex, secondMemberStartIndex);
        }
        private static void SaSwapPostfix(Character firstCharacter, Character secondCharacter, int firstMemberStartIndex, int secondMemberStartIndex, string[] __state, bool __runOriginal)
        {
            if (__runOriginal) SquadActs?.HostSwapped(firstCharacter, secondCharacter, firstMemberStartIndex, secondMemberStartIndex, __state);
        }
        private static void SaExchangeEndPrefix(Controllers.Exchange.ExchangeWithSquadOffer __instance) => SquadActs?.GuestExchangeEnding(__instance);
        private static void SaExchangeEndPostfix() => SquadActs?.GuestExchangeEnded();
        // Group.SetGroupAsEmpty() (privat, aus CheckEmptiness)
        private static bool SaEmptyPrefix(Group __instance) => SquadActs == null || SquadActs.AllowSetEmpty(__instance);
        private static void SaEmptyPostfix(Group __instance, bool __runOriginal)
        {
            if (__runOriginal) SquadActs?.HostSetEmpty(__instance);
        }
        private static bool SaDiscardPrefix(Controllers.ResourceDiscardController __instance) => SquadActs == null || SquadActs.GuestDiscard(__instance);

        // Häuser Teil 2 (BuildingSync)
        private static bool BldPanelCancel(UI.InfoPanels.SpecificPanelInfo __instance) => Buildings == null || Buildings.GuestPanel(__instance, false);
        private static bool BldPanelResume(UI.InfoPanels.SpecificPanelInfo __instance) => Buildings == null || Buildings.GuestPanel(__instance, true);
        private static bool BldPanelAction(UI.InfoPanels.SpecificPanelInfo __instance, UI.InfoPanels.NestedUIElements.InfoPanelAction infoPanelAction)
            => infoPanelAction != UI.InfoPanels.NestedUIElements.InfoPanelAction.CancelInfoPanelButton || Buildings == null || Buildings.GuestPanel(__instance, false);
        private static bool BldExplosivesPrefix(UI.InfoPanels.SpecificPanelInfo __instance) => Buildings == null || Buildings.GuestPlantExplosives(__instance);
        private static bool BldClaimPrefix(Gameplay.Rebuilding.Structure __instance)
            => Buildings == null || Buildings.GuestAction(__instance, BuildingSync.Action.Claim, null, 0f);
        private static bool BldDeconstructPrefix(Gameplay.Buildings.Building __instance)
            => Buildings == null || Buildings.GuestAction(__instance, BuildingSync.Action.Deconstruct, null, 0f);
        private static bool BldCreatePrefix(Controllers.CreationBuildingController __instance) => Buildings == null || Buildings.OnCreateConfirmed(__instance);
        private static bool BldSplitPrefix(Gameplay.Rebuilding.Split.BuildingSplitController __instance) => Buildings == null || Buildings.GuestSplitConfirm(__instance);
        private static bool BldSplitExecPrefix(Gameplay.Rebuilding.Split.SplitRequest splitRequest) => Buildings == null || Buildings.OnSplitExecute(splitRequest);

        // BuildResSync: Fortschritt vom Host setzen, ohne dass das Spiel dabei Lebenspunkte addiert oder eine Reparatur abschließt.
        private static bool BuildResHealthPrefix() => !BuildResSync.Applying;

        // BuildResSync: beim Gast sind UsedResources vom Host übernommen; ConstructableWork.DeleteWork (und die Überschreibungen)
        // ließen sie sonst als Kisten fallen bzw. legten sie zurück ins Haus. Was zurückkommt, rechnet der Host.
        private static void BuildResDeletePrefix(Gameplay.Units.Workers.WorkSystem.Works.ConstructableWork __instance)
        {
            if (Puppet && __instance != null && __instance.UsedResources != null)
                __instance.UsedResources.Clear();
        }

        // PathDrawer.UpdateNextPaths(List<OrderLine> path): beim Gast nur aus SquadViewSync
        private static bool SvNextPathsPrefix() => SquadViewSync.AllowNextPaths();

        // ResStatsSync: MainResourcesContainer.ForceAddResource/TryRemoveResource beim Gast ohne eigene Tages-Statistik
        private static void ResStatsLockPrefix(MainResourcesContainer __instance, ResourceID resourceID, out float[] __state)
        {
            __state = ResStatsSync.LockBefore(__instance, resourceID, Puppet);
        }

        private static void ResStatsLockPostfix(MainResourcesContainer __instance, ResourceID resourceID, float[] __state)
        {
            ResStatsSync.LockAfter(__instance, resourceID, __state);
        }

        // ResStatsSync: MainResourcesContainer.TimeOfDayChanged schiebt beim Gast nichts in den Vortag
        private static void ResStatsDayPrefix(MainResourcesContainer __instance, out System.Collections.Generic.Dictionary<ResourceID, float>[] __state)
        {
            __state = ResStatsSync.DayBefore(__instance, Puppet);
        }

        private static void ResStatsDayPostfix(MainResourcesContainer __instance, System.Collections.Generic.Dictionary<ResourceID, float>[] __state)
        {
            ResStatsSync.DayAfter(__instance, __state);
        }

        // Trupp-Markierung „zuletzt gesteuert“ (SquadViewSync)
        private static SquadViewSync SquadViewsS => Plugin.Instance?.Session?.SquadViews;
        private static void SvGroupPrefix(Group __instance) => SquadViewsS?.NoteUiChange(__instance);
        private static void SvStancePrefix(StancesHandler __instance) => SquadViewsS?.NoteStanceChange(__instance);
        private static void SvPanelPrefix(UI.InfoPanels.SquadPanelInfo __instance) => SquadViewsS?.NoteUiChange(__instance != null ? __instance.Squad : null);
        private static void SvSplitPrefix(Group mainSquad) => SquadViewsS?.NoteUiChange(mainSquad);
        private static void SvPlacePrefix(Character firstCharacter, Group secondGroup)
            => SquadViewsS?.NoteUiChange(firstCharacter != null ? firstCharacter.Group : null, secondGroup);
        private static void SvSwapPrefix(Character firstCharacter, Character secondCharacter)
            => SquadViewsS?.NoteUiChange(firstCharacter != null ? firstCharacter.Group : null, secondCharacter != null ? secondCharacter.Group : null);
        private static void SvOpenPanelsPrefix() => SquadViewsS?.NoteOpenPanelsChange();

        // Gemeinsamer Sperr-Prefix. Bei übersprungener Methode liefert Harmony den Standardwert
        // (false bzw. null) als Ergebnis.
        private static bool BlockWhenPuppet()
        {
            return !Puppet || UnitSync.NetApplying;
        }

        // Sperrt beim Gast schon während des Ladens (BlockWhenPuppet greift erst „im Spiel“).
        private static bool BlockWhenGuest()
        {
            var s = Plugin.Instance?.Session;
            if (s == null || s.Role != Role.Guest || UnitSync.NetApplying)
                return true;
            GuestLairsBlocked++;
            return false;
        }
        public static int GuestLairsBlocked { get; private set; }

        // Wie BlockWhenPuppet, aber auch während der Gast Host-Ereignisse nachspielt.
        private static bool BlockAlwaysWhenPuppet()
        {
            return !Puppet;
        }

        // Für Coroutinen: statt null eine leere Aufzählung, damit StartCoroutine nicht scheitert.
        private static bool BlockCoroutineWhenPuppet(ref IEnumerator __result)
        {
            if (!Puppet || UnitSync.NetApplying)
                return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty()
        {
            yield break;
        }

        // Group.CustomUpdate()
        private static bool GroupUpdatePrefix(Group __instance)
        {
            var units = Units;
            if (units == null || !units.PuppetMode)
                return true;
            try
            {
                units.PuppetTick(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Marionetten-Takt: " + e.Message);
            }
            return false;
        }

        // GroupBuilder.Build(GroupDraft groupDraft)
        private static bool BuildPrefix(GroupDraft groupDraft)
        {
            if (!Puppet || UnitSync.NetApplying || groupDraft == null)
                return true;
            // Eigene Trupps darf der Gast weiter bauen (Stufe 1 gleicht neue Trupps noch nicht ab).
            return groupDraft.Fraction == Fraction.Player;
        }

        private static void BuildPostfix(Group __result)
        {
            if (__result == null)
                return;
            try
            {
                Units?.OnGroupBuilt(__result);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Neue Gruppe melden: " + e.Message);
            }
        }
    }
}
