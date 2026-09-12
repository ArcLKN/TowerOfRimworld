// TowerOfRimworld.cs
using Verse;
using RimWorld;
using static Verse.DamageWorker;
using UnityEngine;
using Verse.Sound;

namespace Tower_of_Rimworld
{

    public class TowerOfRimworldConfig : Mod
    {
        public TowerOfRimworldConfig(ModContentPack content) : base(content)
        {
            Log.Message("Tower Of Rimworld enabled");
        }
    }

    public class ShinsuConstitutionGene : Gene
    {
        public override void PostAdd()
        {
            base.PostAdd();
            AddHediff();
        }
        public override void PostRemove()
        {
            Hediff hediff = GetHediff();
            if (hediff != null)
            {
                base.pawn.health.RemoveHediff(hediff);
            }
        }

        public Hediff GetHediff()
        {
            return base.pawn.health.hediffSet.GetFirstHediffOfDef(ToRDefOf.ToR_Hediff_ShinsuConstitution, false);
        }

        public void AddHediff()
        {
            if (GetHediff() == null)
            {
                Hediff HediffShinsuConstitution = HediffMaker.MakeHediff(ToRDefOf.ToR_Hediff_ShinsuConstitution, base.pawn);
                base.pawn.health.AddHediff(HediffShinsuConstitution);
            }
        }
    }

    public class Building_ActivationVat : Building_Enterable, IStoreSettingsParent, IThingHolderWithDrawnPawn, IThingHolder
    {
        private int ticksRemaining;

        private int powerCutTicks;

        [Unsaved(false)]
        private CompPowerTrader cachedPowerComp;

        [Unsaved(false)]
        private Texture2D cachedInsertPawnTex;

        [Unsaved(false)]
        private Sustainer sustainerWorking;

        [Unsaved(false)]
        private Effecter progressBar;

        private const int TicksToExtract = 30000;

        private const int NoPowerEjectCumulativeTicks = 60000;

        private static readonly Texture2D CancelIcon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel");

        private Pawn ContainedPawn
        {
            get
            {
                return innerContainer.FirstOrDefault() as Pawn;
            }
        }

        public bool PowerOn
        {
            get
            {
                return PowerTraderComp.PowerOn;
            }
        }

        private CompPowerTrader PowerTraderComp
        {
            get
            {
                if (cachedPowerComp == null)
                {
                    cachedPowerComp = this.TryGetComp<CompPowerTrader>();
                }
                return cachedPowerComp;
            }
        }

        public override bool IsContentsSuspended
        {
            get
            {
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // 1.6 API surface. The contracts below were read out of the installed 1.6 assemblies
        // (RimWorld.Building_Enterable; vanilla's Building_GeneExtractor for the body, which is what
        // this class copies; and Building_GrowthVat, which declares this class's exact interface
        // set). CanAcceptPawn returns an AcceptanceReport (not a bool) and TryAcceptPawn returns
        // void. The author's rulings of 2026-09-12 are recorded below as decisions with their
        // rationale; the storage tab is the one item still pending and is marked as such.
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public override Vector3 PawnDrawOffset
        {
            // Mechanical: same expression as vanilla's gene extractor. The def is size (1,1), so
            // the division by def.size.x is a no-op today, but keeping the vanilla form means the
            // draw offset stays correct if the footprint ever grows.
            get
            {
                return IntVec3.West.RotatedBy(base.Rotation).ToVector3() / def.size.x;
            }
        }

        public override AcceptanceReport CanAcceptPawn(Pawn pawn)
        {
            // Author's ruling (2026-09-12): colonists, slaves and prisoners; humanlike; not quest
            // lodgers. That is vanilla's pawn-type gate as the gene extractor writes it, minus the
            // extractor's gene-specific checks (has pass-on genes / has non-archite genes / not in a
            // xenogermination coma), which encode gene extraction rather than activation.
            if (!pawn.IsColonist && !pawn.IsSlaveOfColony && !pawn.IsPrisonerOfColony
                && (!pawn.IsColonySubhuman || !pawn.IsGhoul))
            {
                return false;
            }
            // Author's ruling (2026-09-12): the clause in the test above is kept deliberately, not
            // inherited by accident. Worth knowing what it does, though: `(!IsColonySubhuman ||
            // !IsGhoul)` is satisfied only when the pawn is neither a colony subhuman nor a ghoul,
            // so a *colony subhuman that is a ghoul* still passes the pawn-type gate. If the intent
            // is to refuse those outright, this one clause is what to delete.
            if (selectedPawn != null && selectedPawn != pawn)
            {
                return false;
            }
            if (!pawn.RaceProps.Humanlike || pawn.IsQuestLodger())
            {
                return false;
            }
            // Author's ruling (2026-09-12): activation requires power, matching the def's 500 W
            // CompPowerTrader and vanilla's own gate.
            if (!PowerOn)
            {
                return "NoPower".Translate().CapitalizeFirst();
            }
            if (innerContainer.Count > 0)
            {
                return "Occupied".Translate();
            }
            // Author's ruling (2026-09-12): activation consumes one Thorn — the placeholder the
            // author chose for the "special item" the def's description mentions, with a demon-soul
            // style item planned to follow. See the ingredient block below for why this is
            // absent-safe and where the item is taken from.
            if (!HasActivationIngredient())
            {
                return ActivationIngredientMissingReason();
            }
            return true;
        }

        public override void TryAcceptPawn(Pawn pawn)
        {
            // The vanilla gene extractor's entry sequence, using this class's own TicksToExtract
            // constant for the work length.
            if (CanAcceptPawn(pawn).Accepted)
            {
                selectedPawn = pawn;
                bool wasDraftedOrSelected = pawn.DeSpawnOrDeselect();
                if (innerContainer.TryAddOrTransfer(pawn))
                {
                    startTick = Find.TickManager.TicksGame;
                    ticksRemaining = TicksToExtract;
                    // Author's ruling (2026-09-12): the ingredient is spent when activation *starts*,
                    // which is how vanilla bills consume their ingredients — not at the end, so a
                    // cancelled run does not hand the item back.
                    ConsumeActivationIngredient();
                }
                if (wasDraftedOrSelected)
                {
                    Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
                }
            }
        }

        // ── The activation ingredient (the "special item") ──────────────────────────────────────
        // Author's ruling (2026-09-12): activation consumes one Thorn, standing in until the item
        // system exists (a demon-soul style item is planned). Two implementation choices worth
        // spelling out:
        //
        // 1. ABSENT-SAFE BY LOOKUP, NOT BY XML. The def is resolved by name at runtime with
        //    GetNamedSilentFail and never in a static initializer, so nothing runs before defs are
        //    loaded. On a tree that does not ship ToR_Thorn — main today, and this branch — the
        //    requirement simply does not exist: no load error, no red log, and the vat behaves
        //    exactly as it did before. That is deliberate: the class and the item currently live on
        //    different branches, and a hard DefDatabase.GetNamed would break every tree that has the
        //    vat without the thorn.
        // 2. TAKEN FROM THE COLONY'S STOCKPILES, not from inside the building. The def declares no
        //    storage and the class exposes no player-facing way to load an item into it, so
        //    demanding the item *inside* the vat would make it unstartable. Taking it from the map
        //    also keeps this requirement independent of the still-pending storage-tab decision. If
        //    the design later wants the thorn hauled into the vat, that belongs on the def (a
        //    refuelable-style comp) rather than in this class.
        private const string ActivationIngredientDefName = "ToR_Thorn";

        private static ThingDef ActivationIngredientDef
        {
            get
            {
                return DefDatabase<ThingDef>.GetNamedSilentFail(ActivationIngredientDefName);
            }
        }

        private bool HasActivationIngredient()
        {
            ThingDef ingredient = ActivationIngredientDef;
            return ingredient == null || AvailableActivationIngredient(ingredient) != null;
        }

        private string ActivationIngredientMissingReason()
        {
            // Composed from vanilla's own "Requires" string plus the ingredient's own label, which is
            // translatable through the item's DefInjected entry — so this invents no English key and
            // still reads in French for a thorn with a French label.
            return "BillRequires".Translate() + " " + ActivationIngredientDef.label;
        }

        private Thing AvailableActivationIngredient(ThingDef ingredient)
        {
            if (base.Map == null)
            {
                return null;
            }
            foreach (Thing thing in base.Map.listerThings.ThingsOfDef(ingredient))
            {
                if (thing.Spawned && !thing.IsForbidden(Faction.OfPlayer))
                {
                    return thing;
                }
            }
            return null;
        }

        private void ConsumeActivationIngredient()
        {
            ThingDef ingredient = ActivationIngredientDef;
            if (ingredient == null)
            {
                return;
            }
            Thing thing = AvailableActivationIngredient(ingredient);
            if (thing == null)
            {
                return;
            }
            // The Thorn's stackLimit is 1 today; splitting keeps this correct if that is ever raised,
            // rather than silently eating a whole stack.
            if (thing.stackCount > 1)
            {
                thing.SplitOff(1).Destroy();
            }
            else
            {
                thing.Destroy();
            }
        }

        // IThingHolderWithDrawnPawn — mechanical, taken from vanilla's gene extractor so the
        // contained pawn is drawn lying inside the vat.

        public float HeldPawnDrawPos_Y
        {
            get
            {
                return DrawPos.y + 0.03658537f;
            }
        }

        public float HeldPawnBodyAngle
        {
            get
            {
                return base.Rotation.Opposite.AsAngle;
            }
        }

        public PawnPosture HeldPawnPosture
        {
            get
            {
                return PawnPosture.LayingOnGroundFaceUp;
            }
        }

        // IStoreSettingsParent — kept because the class declares the interface, but read the
        // StorageTabVisible decision below before assuming this vat stores anything.
        // Runtime-only: the base ExposeData only scribes innerContainer/startTick/selectedPawn, so
        // this is never written to a save (and nothing player-editable lives in it).

        private StorageSettings storeSettings;

        public StorageSettings GetStoreSettings()
        {
            // Returns a lazily-created settings object purely so no caller can hit a null. The
            // player cannot reach it: see StorageTabVisible.
            if (storeSettings == null)
            {
                storeSettings = new StorageSettings(this);
            }
            return storeSettings;
        }

        public StorageSettings GetParentStoreSettings()
        {
            // Vanilla's fallback for a building with no fixed storage settings
            // (Building_Storage.GetParentStoreSettings does the same).
            return def.building.fixedStorageSettings ?? StorageSettings.EverStorableFixedSettings();
        }

        public void Notify_SettingsChanged()
        {
            // No-op by design: this building has no slot group or haul destination to notify
            // (Building_Storage notifies its slot group here), and the storage tab is hidden, so
            // nothing in-game can change these settings in the first place.
        }

        public bool StorageTabVisible
        {
            // USER DECISION (StorageTabVisible = false) — the vat is a machine with a fixed input
            // (one pawn in innerContainer), not a player-managed container: the def declares no
            // CompProperties_Storage, no fixedStorageSettings and no storage tags. Showing an empty
            // storage tab would offer controls that do nothing. Set this to true — and revisit
            // GetStoreSettings above — if the vat is ever meant to hold items the player manages.
            get
            {
                return false;
            }
        }

        private void Cancel()
        {
            startTick = -1;
            selectedPawn = null;
            sustainerWorking = null;
            powerCutTicks = 0;
            innerContainer.TryDropAll(def.hasInteractionCell ? InteractionCell : base.Position, base.Map, ThingPlaceMode.Near);
        }

        private void TickEffects()
        {
            if (sustainerWorking == null || sustainerWorking.Ended)
            {
                sustainerWorking = SoundDefOf.GeneExtractor_Working.TrySpawnSustainer(SoundInfo.InMap(this, MaintenanceType.PerTick));
            }
            else
            {
                sustainerWorking.Maintain();
            }
            if (progressBar == null)
            {
                progressBar = EffecterDefOf.ProgressBarAlwaysVisible.Spawn();
            }
            progressBar.EffectTick(new TargetInfo(base.Position + IntVec3.North.RotatedBy(base.Rotation), base.Map), TargetInfo.Invalid);
            MoteProgressBar mote = ((SubEffecter_ProgressBar)progressBar.children[0]).mote;
            if (mote != null)
            {
                mote.progress = 1f - Mathf.Clamp01((float)ticksRemaining / 30000f);
                mote.offsetZ = ((base.Rotation == Rot4.North) ? 0.5f : (-0.5f));
            }
        }



        protected override void Tick()
        {
            base.Tick();
            if (this.IsHashIntervalTick(250))
            {
                PowerTraderComp.PowerOutput = (base.Working ? (0f - base.PowerComp.Props.PowerConsumption) : (0f - base.PowerComp.Props.idlePowerDraw));
            }
            if (base.Working)
            {
                if (ContainedPawn == null)
                {
                    Cancel();
                    return;
                }
                if (PowerTraderComp.PowerOn)
                {
                    TickEffects();
                    if (PowerOn)
                    {
                        ticksRemaining--;
                    }
                    if (ticksRemaining <= 0)
                    {
                        Finish();
                    }
                    return;
                }
                powerCutTicks++;
                if (powerCutTicks >= 60000)
                {
                    Pawn containedPawn = ContainedPawn;
                    if (containedPawn != null)
                    {
                        Messages.Message("GeneExtractorNoPowerEjectedMessage".Translate(containedPawn.Named("PAWN")), containedPawn, MessageTypeDefOf.NegativeEvent, false);
                    }
                    Cancel();
                }
            }
            else
            {
                if (selectedPawn != null && selectedPawn.Dead)
                {
                    Cancel();
                }
                if (progressBar != null)
                {
                    progressBar.Cleanup();
                    progressBar = null;
                }
            }
        }

        private void Finish ()
        {

            selectedPawn = null;
            sustainerWorking = null;
            powerCutTicks = 0;
            if (ContainedPawn != null)
            {
                Pawn containedPawn = ContainedPawn;

                HediffDef hediffDef = ToRDefOf.ToR_Hediff_ShinsuConstitution;
                if (hediffDef != null)
                {
                    Hediff hediff = HediffMaker.MakeHediff(hediffDef, containedPawn);
                    containedPawn.health.AddHediff(hediff);
                }
                IntVec3 intVec = (def.hasInteractionCell ? InteractionCell : base.Position);
                innerContainer.TryDropAll(intVec, base.Map, ThingPlaceMode.Near);

                SoundDefOf.PsychicPulseGlobal.PlayOneShot(new TargetInfo(Position, Map));
            }
        }
    }

    [DefOf]
    public static class ToRDefOf
    {
        public static HediffDef ToR_Hediff_ShinsuConstitution;

        static ToRDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ToRDefOf));
        }
    }

}
