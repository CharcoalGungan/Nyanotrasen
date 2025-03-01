using Content.Shared.Disease;
using Content.Shared.Disease.Components;
using Content.Shared.Materials;
using Content.Shared.Research.Components;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Tag;
using Content.Shared.Toggleable;
using Content.Shared.DoAfter;
using Content.Server.Disease.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.DoAfter;
using Content.Server.Research.Systems;
using Content.Server.UserInterface;
using Content.Server.Construction;
using Content.Server.Popups;
using Robust.Shared.Prototypes;
using Robust.Server.GameObjects;
using Robust.Server.Player;

namespace Content.Server.Disease
{
    public sealed class VaccineSystem : EntitySystem
    {
        [Dependency] private readonly DiseaseDiagnosisSystem _diseaseDiagnosisSystem = default!;
        [Dependency] private readonly SharedMaterialStorageSystem _storageSystem = default!;
        [Dependency] private readonly UserInterfaceSystem _uiSys = default!;
        [Dependency] private readonly ResearchSystem _research = default!;
        [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
        [Dependency] private readonly PopupSystem _popupSystem = default!;
        [Dependency] private readonly DoAfterSystem _doAfterSystem = default!;
        [Dependency] private readonly AudioSystem _audioSystem = default!;
        [Dependency] private readonly TagSystem _tagSystem = default!;
        [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, ResearchRegistrationChangedEvent>(OnResearchRegistrationChanged);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, CreateVaccineMessage>(OnCreateVaccineMessageReceived);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, DiseaseMachineFinishedEvent>(OnVaccinatorFinished);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, MaterialAmountChangedEvent>(OnVaccinatorAmountChanged);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, ResearchClientServerSelectedMessage>(OnServerSelected);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, ResearchClientServerDeselectedMessage>(OnServerDeselected);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, VaccinatorSyncRequestMessage>(OnSyncRequest);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, VaccinatorServerSelectionMessage>(OpenServerList);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, AfterActivatableUIOpenEvent>(AfterUIOpen);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, RefreshPartsEvent>(OnRefreshParts);
            SubscribeLocalEvent<DiseaseVaccineCreatorComponent, UpgradeExamineEvent>(OnUpgradeExamine);

            /// vaccines, the item
            SubscribeLocalEvent<DiseaseVaccineComponent, AfterInteractEvent>(OnAfterInteract);
            SubscribeLocalEvent<DiseaseVaccineComponent, ExaminedEvent>(OnExamined);

            SubscribeLocalEvent<DiseaseVaccineComponent, DoAfterEvent>(OnDoAfter);
        }

        private void OnResearchRegistrationChanged(EntityUid uid, DiseaseVaccineCreatorComponent component, ref ResearchRegistrationChangedEvent args)
        {
            if (TryComp<DiseaseServerComponent>(args.Server, out var diseaseServer))
                component.DiseaseServer = diseaseServer;
            else
                component.DiseaseServer = null;
        }

        /// <summary>
        /// Creates a vaccine, if possible, when sent a UI message to do so.
        /// </summary>
        private void OnCreateVaccineMessageReceived(EntityUid uid, DiseaseVaccineCreatorComponent component, CreateVaccineMessage args)
        {
            if (HasComp<DiseaseMachineRunningComponent>(uid) || !this.IsPowered(uid, EntityManager))
                return;

            if (_storageSystem.GetMaterialAmount(uid, "Biomass") < component.BiomassCost * args.Amount)
                return;

            if (!_prototypeManager.TryIndex<DiseasePrototype>(args.Disease, out var disease))
                return;

            if (!disease.Infectious)
                return;

            component.Queued = args.Amount;
            QueueNext(uid, component, disease);
            UpdateUserInterfaceState(uid, component, true);
        }

        private void QueueNext(EntityUid uid, DiseaseVaccineCreatorComponent component, DiseasePrototype disease, DiseaseMachineComponent? machine = null)
        {
            if (!Resolve(uid, ref machine))
                return;

            machine.Disease = disease;
            _diseaseDiagnosisSystem.AddQueue.Enqueue(uid);
            _diseaseDiagnosisSystem.UpdateAppearance(uid, true, true);
            _audioSystem.PlayPvs(component.RunningSoundPath, uid);
        }

        /// <summary>
        /// Prints a vaccine that will vaccinate
        /// against the disease on the inserted swab.
        /// </summary>
        private void OnVaccinatorFinished(EntityUid uid, DiseaseVaccineCreatorComponent component, DiseaseMachineFinishedEvent args)
        {
            _diseaseDiagnosisSystem.UpdateAppearance(uid, this.IsPowered(uid, EntityManager), false);

            if (!_storageSystem.TryChangeMaterialAmount(uid, "Biomass", (0 - component.BiomassCost)))
                return;

            // spawn a vaccine
            var vaxx = Spawn(args.Machine.MachineOutput, Transform(uid).Coordinates);

            if (args.Machine.Disease == null)
                return;

            MetaData(vaxx).EntityName = Loc.GetString("vaccine-name", ("disease", args.Machine.Disease.Name));
            MetaData(vaxx).EntityDescription = Loc.GetString("vaccine-desc", ("disease", args.Machine.Disease.Name));

            if (!TryComp<DiseaseVaccineComponent>(vaxx, out var vaxxComp))
                return;

            vaxxComp.Disease = args.Machine.Disease;

            component.Queued--;
            if (component.Queued > 0)
            {
                args.Dequeue = false;
                QueueNext(uid, component, args.Machine.Disease, args.Machine);
                UpdateUserInterfaceState(uid, component);
            }
            else
            {
                UpdateUserInterfaceState(uid, component, false);
            }
        }

        private void OnVaccinatorAmountChanged(EntityUid uid, DiseaseVaccineCreatorComponent component, ref MaterialAmountChangedEvent args)
        {
            UpdateUserInterfaceState(uid, component);
        }

        private void OnServerSelected(EntityUid uid, DiseaseVaccineCreatorComponent component, ResearchClientServerSelectedMessage args)
        {
            if (!_research.TryGetServerById(args.ServerId, out var serverUid, out var serverComponent))
                return;

            if (!TryComp<DiseaseServerComponent>(serverUid, out var diseaseServer))
                return;

            component.DiseaseServer = diseaseServer;
            UpdateUserInterfaceState(uid, component);
        }

        private void OnServerDeselected(EntityUid uid, DiseaseVaccineCreatorComponent component, ResearchClientServerDeselectedMessage args)
        {
            component.DiseaseServer = null;
            UpdateUserInterfaceState(uid, component);
        }

        private void OnSyncRequest(EntityUid uid, DiseaseVaccineCreatorComponent component, VaccinatorSyncRequestMessage args)
        {
            UpdateUserInterfaceState(uid, component);
        }

        private void OpenServerList(EntityUid uid, DiseaseVaccineCreatorComponent component, VaccinatorServerSelectionMessage args)
        {
            _uiSys.TryOpen(uid, ResearchClientUiKey.Key, (IPlayerSession) args.Session);
        }

        private void AfterUIOpen(EntityUid uid, DiseaseVaccineCreatorComponent component, AfterActivatableUIOpenEvent args)
        {
            UpdateUserInterfaceState(uid, component);
        }

        private void OnRefreshParts(EntityUid uid, DiseaseVaccineCreatorComponent component, RefreshPartsEvent args)
        {
            int costRating = (int) args.PartRatings[component.MachinePartCost];

            component.BiomassCost = component.BaseBiomassCost - costRating;
            UpdateUserInterfaceState(uid, component);
        }

        private void OnUpgradeExamine(EntityUid uid, DiseaseVaccineCreatorComponent component, UpgradeExamineEvent args)
        {
            args.AddNumberUpgrade("vaccine-machine-cost-upgrade", component.BiomassCost - component.BaseBiomassCost + 1);
        }

        public void UpdateUserInterfaceState(EntityUid uid, DiseaseVaccineCreatorComponent? component = null, bool? overrideLocked = null)
        {
            if (!Resolve(uid, ref component))
                return;

            var ui = _uiSys.GetUi(uid, VaccineMachineUiKey.Key);
            var biomass = _storageSystem.GetMaterialAmount(uid, "Biomass");

            var diseases = new List<(string id, string name)>();
            var hasServer = false;

            if (component.DiseaseServer != null)
            {
                foreach (var disease in component.DiseaseServer.Diseases)
                {
                    if (!disease.Infectious)
                        continue;

                    diseases.Add((disease.ID, disease.Name));
                }

                hasServer = true;
            }

            var state = new VaccineMachineUpdateState(biomass, component.BiomassCost, diseases, overrideLocked ?? HasComp<DiseaseMachineRunningComponent>(uid), hasServer);
            _uiSys.SetUiState(ui, state);
        }

        /// <summary>
        /// Called when a vaccine is used on someone
        /// to handle the vaccination doafter
        /// </summary>
        private void OnAfterInteract(EntityUid uid, DiseaseVaccineComponent vaxx, AfterInteractEvent args)
        {
            if (args.Target == null || !args.CanReach)
                return;

            if (vaxx.Used)
            {
                _popupSystem.PopupEntity(Loc.GetString("vaxx-already-used"), args.User, args.User);
                return;
            }

            _doAfterSystem.DoAfter(new DoAfterEventArgs(args.User, vaxx.InjectDelay, target: args.Target, used:uid)
            {
                BreakOnTargetMove = true,
                BreakOnUserMove = true,
                BreakOnStun = true,
                NeedHand = true
            });
        }

        /// <summary>
        /// Called when a vaccine is examined.
        /// Currently doesn't do much because
        /// vaccines don't have unique art with a seperate
        /// state visualizer.
        /// </summary>
        private void OnExamined(EntityUid uid, DiseaseVaccineComponent vaxx, ExaminedEvent args)
        {
            if (args.IsInDetailsRange)
            {
                if (vaxx.Used)
                    args.PushMarkup(Loc.GetString("vaxx-used"));
                else
                    args.PushMarkup(Loc.GetString("vaxx-unused"));
            }
        }

        /// <summary>
        /// Adds a disease to the carrier's
        /// past diseases to give them immunity
        /// IF they don't already have the disease.
        /// </summary>
        public void Vaccinate(DiseaseCarrierComponent carrier, DiseasePrototype disease)
        {
            foreach (var currentDisease in carrier.Diseases)
            {
                if (currentDisease.ID == disease.ID) //ID because of the way protoypes work
                    return;
            }
            carrier.PastDiseases.Add(disease);
        }

        private void OnDoAfter(EntityUid uid, DiseaseVaccineComponent component, DoAfterEvent args)
        {
            if (args.Handled || args.Cancelled || !TryComp<DiseaseCarrierComponent>(args.Args.Target, out var carrier) || component.Disease == null)
                return;

            Vaccinate(carrier, component.Disease);
            EntityManager.DeleteEntity(uid);
            args.Handled = true;
        }
    }
}
