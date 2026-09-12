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
