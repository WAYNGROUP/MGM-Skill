﻿using System.Linq;

using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace WaynGroup.Mgm.Ability
{


    /// <summary>
    /// Base system to trigger effects. It provides the shared functionality of checking which ability is active and which target(s) are affected.
    /// </summary>
    /// <typeparam name="EFFECT_BUFFER">The effect buffer that stores all the effect of a EFFECT type with it's corresponding ability index.</typeparam>
    /// <typeparam name="EFFECT">The effect type trigered by this system.</typeparam>
    /// <typeparam name="CONSUMER">The system in charge of consuming the effects once triggered.</typeparam>
    /// <typeparam name="CTX_WRITER">The writer struct in charge of populating the context surroinding the triggered effect like informations about the caster (position, strength,...).</typeparam>
    /// <typeparam name="EFFECT_CTX">The struct containing the effect and it's context like informations about the caster (position, strength,...)</typeparam>
    [UpdateInGroup(typeof(AbilityTriggerSystemGroup))]
    public abstract class AbilityEffectTriggerSystem<EFFECT_BUFFER, EFFECT, CONSUMER, CTX_WRITER, EFFECT_CTX> : SystemBase
        where EFFECT : struct, IEffect
        where CONSUMER : AbilityEffectConsumerSystem<EFFECT, EFFECT_CTX>
        where EFFECT_BUFFER : struct, IEffectBufferElement<EFFECT>
        where CTX_WRITER : struct, IEffectContextWriter<EFFECT>
        where EFFECT_CTX : struct, IEffectContext<EFFECT>
    {
        /// <summary>
        /// The system in charge of consuming the effects once triggered.
        /// </summary>
        private AbilityEffectConsumerSystem<EFFECT, EFFECT_CTX> _conusmerSystem;

        /// <summary>
        /// The base query to select entity that are eligible to this system.
        /// </summary>
        private EntityQuery _query;

        /// <summary>
        /// A method to describe the necessary components on hte caster to populate the effect's context.
        /// </summary>
        /// <returns>EntityQueryDesc</returns>
        protected virtual EntityQueryDesc GetEffectContextEntityQueryDesc()
        {
            return new EntityQueryDesc();
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _conusmerSystem = World.GetOrCreateSystem<CONSUMER>();
            EntityQueryDesc baseEntityQueryDesc = new EntityQueryDesc()
            {
                All = new ComponentType[]
                {
                        ComponentType.ReadOnly<AbilityBuffer>(),
                        ComponentType.ReadOnly<Target>(),
                        ComponentType.ReadOnly<EFFECT_BUFFER>()
                }
            };

            EntityQueryDesc contextQueryDesc = GetEffectContextEntityQueryDesc();

            EntityQueryDesc entityQueryDesc = new EntityQueryDesc()
            {
                All = baseEntityQueryDesc.All.Concat(contextQueryDesc.All).ToArray(),
                Any = baseEntityQueryDesc.Any.Concat(contextQueryDesc.Any).ToArray(),
                None = baseEntityQueryDesc.None.Concat(contextQueryDesc.None).ToArray(),
                Options = contextQueryDesc.Options
            };


            _query = GetEntityQuery(entityQueryDesc);

        }

        /// <summary>
        /// Job in charge of the shared logic (targetting, ability activity,..).
        /// This job will call the WriteContextualizedEffect method of the CTX_WRITER when the efect has to be triggered.
        /// </summary>
        [BurstCompile]
        private struct TriggerJob : IJobChunk
        {
            public CTX_WRITER EffectContextWriter;
            [ReadOnly] public ArchetypeChunkBufferType<AbilityBuffer> AbilityBufferChunk;
            [ReadOnly] public ArchetypeChunkBufferType<EFFECT_BUFFER> EffectBufferChunk;
            [ReadOnly] public ArchetypeChunkComponentType<Target> TargetChunk;
            [ReadOnly] public ArchetypeChunkEntityType EntityChunk;
            public NativeStream.Writer ConsumerWriter;

            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
            {
                BufferAccessor<AbilityBuffer> abilityBufffers = chunk.GetBufferAccessor(AbilityBufferChunk);
                BufferAccessor<EFFECT_BUFFER> effectBuffers = chunk.GetBufferAccessor(EffectBufferChunk);
                NativeArray<Target> targets = chunk.GetNativeArray(TargetChunk);
                NativeArray<Entity> entities = chunk.GetNativeArray(EntityChunk);
                EffectContextWriter.PrepareChunk(chunk);

                ConsumerWriter.BeginForEachIndex(chunkIndex);
                for (int entityIndex = 0; entityIndex < chunk.Count; ++entityIndex)
                {
                    NativeArray<AbilityBuffer> AbilityBufferArray = abilityBufffers[entityIndex].AsNativeArray();
                    NativeArray<EFFECT_BUFFER> effectBufferArray = effectBuffers[entityIndex].AsNativeArray();
                    for (int abilityIndex = 0; abilityIndex < AbilityBufferArray.Length; ++abilityIndex)
                    {

                        Ability Ability = AbilityBufferArray[abilityIndex];
                        if (Ability.State != AbilityState.Active) continue;
                        for (int effectIndex = 0; effectIndex < effectBufferArray.Length; effectIndex++)
                        {
                            EFFECT_BUFFER EffectBuffer = effectBufferArray[effectIndex];
                            if (EffectBuffer.AbilityIndex != abilityIndex) continue;

                            EffectContextWriter.WriteContextualizedEffect(entityIndex, ref ConsumerWriter, EffectBuffer.Effect, EffectBuffer.Effect.Affects == EffectAffectType.Target ? targets[entityIndex].Value : entities[effectIndex]);

                        }
                    }
                }
                ConsumerWriter.EndForEachIndex();

            }
        }

        /// <summary>
        /// This method delegates the cosntruction of the CTX_WRITER to the derived class.
        /// </summary>
        /// <returns>A struct implementing IEffectContextWriter<EFFECT>.</returns>
        protected abstract CTX_WRITER GetContextWriter();

        protected override void OnUpdate()
        {
            // If the consumer won't run, there is no point in tirgerring the effects...
            // This also avoid the creation of a stream that would never be disposed of.
            if (!_conusmerSystem.ShouldRunSystem()) return;

            Dependency = new TriggerJob()
            {
                EffectBufferChunk = GetArchetypeChunkBufferType<EFFECT_BUFFER>(true),
                AbilityBufferChunk = GetArchetypeChunkBufferType<AbilityBuffer>(true),
                TargetChunk = GetArchetypeChunkComponentType<Target>(true),
                EntityChunk = GetArchetypeChunkEntityType(),
                ConsumerWriter = _conusmerSystem.CreateConsumerWriter(_query.CalculateChunkCount()),
                EffectContextWriter = GetContextWriter()
            }.ScheduleParallel(_query, Dependency);

            // Tell the consumer to wait for ths trigger job to finish before starting to consume the effects.
            _conusmerSystem.RegisterTriggerDependency(Dependency);
        }
    }

}
