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
        // (RimWorld.Building_Enterable, and vanilla's Building_GeneExtractor / Building_Storage as
        // the closest analogues — this class is a copy of the gene extractor). Note that
        // CanAcceptPawn returns an AcceptanceReport (not a bool) and TryAcceptPawn returns void.
        // Every judgement call is marked USER DECISION so nothing here is silently invented.
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
            // USER DECISION (who may enter) — this mirrors the pawn-type gate of vanilla's gene
            // extractor: colonists, slaves and prisoners, humanlike, not quest lodgers. The gene
            // extractor's own extra checks (has pass-on genes / has non-archite genes / not in a
            // xenogermination coma) are deliberately NOT copied: those encode gene extraction, not
            // activation. Widen or narrow this list when the activation design is settled.
            // The `(!IsColonySubhuman || !IsGhoul)` clause is inherited verbatim from vanilla;
            // whether a colony ghoul should be activatable is a design question, not a technical one.
            if (!pawn.IsColonist && !pawn.IsSlaveOfColony && !pawn.IsPrisonerOfColony
                && (!pawn.IsColonySubhuman || !pawn.IsGhoul))
            {
                return false;
            }
            if (selectedPawn != null && selectedPawn != pawn)
            {
                return false;
            }
            if (!pawn.RaceProps.Humanlike || pawn.IsQuestLodger())
            {
                return false;
            }
            // USER DECISION (power) — activation needs power, matching both the def's 500 W
            // CompPowerTrader and vanilla's own gate. If the vat should instead run unpowered,
            // this is the line to change (and the def's power comp to drop).
            if (!PowerOn)
            {
                return "NoPower".Translate().CapitalizeFirst();
            }
            if (innerContainer.Count > 0)
            {
                return "Occupied".Translate();
            }
            // USER DECISION — UNIMPLEMENTED HOOK. The vat def's description says it "requires a
            // special item to function". That item does not exist yet, so no requirement is
            // enforced here on purpose. This is exactly where the check belongs once it is built.
            return true;
        }

        public override void TryAcceptPawn(Pawn pawn)
        {
            // Mechanical: the vanilla gene extractor's entry sequence, using this class's own
            // TicksToExtract constant for the work length.
            if (CanAcceptPawn(pawn).Accepted)
            {
                selectedPawn = pawn;
                bool wasDraftedOrSelected = pawn.DeSpawnOrDeselect();
                if (innerContainer.TryAddOrTransfer(pawn))
                {
                    startTick = Find.TickManager.TicksGame;
                    ticksRemaining = TicksToExtract;
                }
                if (wasDraftedOrSelected)
                {
                    Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
                }
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
