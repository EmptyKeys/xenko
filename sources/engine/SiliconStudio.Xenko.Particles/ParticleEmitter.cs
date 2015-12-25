﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using SiliconStudio.Core;
using SiliconStudio.Core.Annotations;
using SiliconStudio.Core.Collections;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Xenko.Graphics;
using SiliconStudio.Xenko.Particles.Initializers;
using SiliconStudio.Xenko.Particles.Materials;
using SiliconStudio.Xenko.Particles.Modules;
using SiliconStudio.Xenko.Particles.ShapeBuilders;
using SiliconStudio.Xenko.Particles.Sorters;
using SiliconStudio.Xenko.Particles.Spawners;
using SiliconStudio.Xenko.Particles.VertexLayouts;
using SiliconStudio.Xenko.Rendering;

namespace SiliconStudio.Xenko.Particles
{
    public enum EmitterRandomSeedMethod : byte
    {
        Time = 0,
        Fixed = 1,
        Position = 2,        
    }

    public enum EmitterSimulationSpace : byte
    {
        World = 0,
        Local = 1,
    }

    public enum EmitterSortingPolicy : byte
    {
        None = 0,
        ByDepth = 1,
        ByAge = 2,
    }


    [DataContract("ParticleEmitter")]
    public class ParticleEmitter
    {
        /// <summary>
        /// Gets or sets a value indicating whether this <see cref="ParticleEmitter"/> is enabled.
        /// </summary>
        /// <value>
        ///   <c>true</c> if enabled; otherwise, <c>false</c>.
        /// </value>
        [DataMember(-10)]
        [DefaultValue(true)]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// List of <see cref="SpawnerBase"/> to spawn particles in this <see cref="ParticleEmitter"/>
        /// </summary>
        /// <userdoc>
        /// Spawners define when, how and how many particles are spawned withing this Emitter. There can be several of them.
        /// </userdoc>
        [DataMember(30)]
        [Display("Spawners")]
        [NotNullItems]
        [MemberCollection(CanReorderItems = true)]
        public readonly TrackingCollection<SpawnerBase> Spawners;

        // Exposing for debug drawing
        [DataMemberIgnore]
        public readonly ParticlePool pool;

        [DataMemberIgnore]
        private Vector3 depthSortVector = new Vector3(0, 0, -1);

        [DataMemberIgnore]
        internal ParticleSorter ParticleSorter;

        private void PoolChangedNotification()
        {
            if (SortingPolicy == EmitterSortingPolicy.None || pool.ParticleCapacity <= 0)
            {
                ParticleSorter = new ParticleSorterDefault(pool);
                return;
            }

            if (SortingPolicy == EmitterSortingPolicy.ByDepth)
            {
                GetSortIndex<Vector3> sortByDepth = value =>
                {
                    var depth = Vector3.Dot(depthSortVector, value);
                    return depth;
                };

                ParticleSorter = new ParticleSorterCustom<Vector3>(pool, ParticleFields.Position, sortByDepth);
                return;
            }

            if (SortingPolicy == EmitterSortingPolicy.ByAge)
            {
                GetSortIndex<float> sortByAge = value => { return -value; };

                ParticleSorter = new ParticleSorterCustom<float>(pool, ParticleFields.Life, sortByAge);
                return;
            }

            // Default - no sorting
            ParticleSorter = new ParticleSorterDefault(pool);
        }

        private EmitterSortingPolicy sortingPolicy = EmitterSortingPolicy.None;

        /// <summary>
        /// How and if particles are sorted, and how they are access during rendering
        /// </summary>
        /// <userdoc>
        /// Choose if the particles should be soretd by depth (visually correct), age or not at all (fastest, good for additive blending)
        /// </userdoc>
        [DataMember(35)]
        [Display("Sorting")]
        public EmitterSortingPolicy SortingPolicy
        {
            get { return sortingPolicy; }
            set
            {
                sortingPolicy = value;
                PoolChangedNotification();
            }
        }

        [DataMemberIgnore]
        internal ParticleRandomSeedGenerator RandomSeedGenerator;

        public ParticleEmitter()
        {
            pool = new ParticlePool(0, 0);
            PoolChangedNotification();
            requiredFields = new Dictionary<ParticleFieldDescription, int>();

            // For now all particles require Life and RandomSeed fields, always
            AddRequiredField(ParticleFields.RemainingLife);
            AddRequiredField(ParticleFields.RandomSeed);
            AddRequiredField(ParticleFields.Position);

            initialDefaultFields = new InitialDefaultFields();

            Initializers = new TrackingCollection<Initializer>();
            Initializers.CollectionChanged += ModulesChanged;

            Updaters = new TrackingCollection<UpdaterBase>();
            Updaters.CollectionChanged += ModulesChanged;

            Spawners = new TrackingCollection<SpawnerBase>();
            Spawners.CollectionChanged += SpawnersChanged;        
        }

        #region Modules

        /// <summary>
        /// List of <see cref="Initializer"/> within thie <see cref="ParticleEmitter"/>. Adjust <see cref="requiredFields"/> automatically
        /// </summary>
        /// <userdoc>
        /// Initializers set initial values for fields of particles which just spawned. Have no effect on already spawned particles.
        /// </userdoc>
        [DataMember(200)]
        [Display("Initializers")]
        [NotNullItems]
        [MemberCollection(CanReorderItems = true)]
        public readonly TrackingCollection<Initializer> Initializers;

        [DataMemberIgnore]
        private readonly InitialDefaultFields initialDefaultFields;

        /// <summary>
        /// List of <see cref="UpdaterBase"/> within thie <see cref="ParticleEmitter"/>. Adjust <see cref="requiredFields"/> automatically
        /// </summary>
        /// <userdoc>
        /// Updaters change the fields of all living particles every frame, like position, velocity, color, size etc.
        /// </userdoc>
        [DataMember(300)]
        [Display("Updaters")]
        [NotNullItems]
        [MemberCollection(CanReorderItems = true)]
        public readonly TrackingCollection<UpdaterBase> Updaters;

        private void ModulesChanged(object sender, TrackingCollectionChangedEventArgs e)
        {
            var module = e.Item as ParticleModuleBase;
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    module?.RequiredFields.ForEach(AddRequiredField);
                    break;

                case NotifyCollectionChangedAction.Remove:
                    module?.RequiredFields.ForEach(RemoveRequiredField);
                    break;
            }
        }

        private void SpawnersChanged(object sender, TrackingCollectionChangedEventArgs e)
        {
            Dirty = true;
        }
        #endregion

        #region Update

        /// <summary>
        /// Updates the emitter and all its particles, and applies all updaters and spawners.
        /// </summary>
        /// <param name="dt">Delta time, elapsed time since the last call, in seconds</param>
        /// <param name="parentSystem">The parent <see cref="ParticleSystem"/> hosting this emitter</param>
        public void Update(float dt, ParticleSystem parentSystem)
        {
            if (!delayInit)
            {
                DelayedInitialization(parentSystem);
            }

            drawPosition = parentSystem.Translation;
            drawRotation = parentSystem.Rotation;
            drawScale    = parentSystem.UniformScale;

            if (simulationSpace == EmitterSimulationSpace.World)
            {
                // Update sub-systems
                initialDefaultFields.SetParentTRS(ref parentSystem.Translation, ref parentSystem.Rotation, parentSystem.UniformScale);

                foreach (var initializer in Initializers)
                {
                    initializer.SetParentTRS(ref parentSystem.Translation, ref parentSystem.Rotation, parentSystem.UniformScale);
                }

                foreach (var updater in Updaters)
                {
                    updater.SetParentTRS(ref parentSystem.Translation, ref parentSystem.Rotation, parentSystem.UniformScale);
                }
            }
            else
            {
                var posIdentity = new Vector3(0, 0, 0);
                var rotIdentity = new Quaternion(0, 0, 0, 1);

                // Update sub-systems
                initialDefaultFields.SetParentTRS(ref posIdentity, ref rotIdentity, 1f);

                foreach (var initializer in Initializers)
                {
                    initializer.SetParentTRS(ref posIdentity, ref rotIdentity, 1f);
                }

                foreach (var updater in Updaters)
                {
                    updater.SetParentTRS(ref posIdentity, ref rotIdentity, 1f);
                }
            }

            EnsurePoolCapacity();

            MoveAndDeleteParticles(dt);

            ApplyParticleUpdaters(dt);

            SpawnNewParticles(dt);
        }

        [DataMemberIgnore]
        private bool delayInit = false;

        /// <summary>
        /// Some parameters should be initialized when the emitter first runs, rather than in the constructor
        /// </summary>
        protected unsafe void DelayedInitialization(ParticleSystem parentSystem)
        {
            if (delayInit)
                return;

            delayInit = true;

            // RandomNumberGenerator creation
            {
                UInt32 rngSeed = 0; // EmitterRandomSeedMethod.Fixed

                if (randomSeedMethod == EmitterRandomSeedMethod.Time)
                {
                    // Stopwatch has maximum possible frequency, so rngSeeds initialized at different times will be different
                    rngSeed = unchecked((UInt32)Stopwatch.GetTimestamp());
                }
                else if (randomSeedMethod == EmitterRandomSeedMethod.Position)
                {
                    // Different float have different uint representation so randomness should be good
                    // The only problem occurs when the three position components are the same
                    var posX = parentSystem.Translation.X;
                    var posY = parentSystem.Translation.Y;
                    var posZ = parentSystem.Translation.Z;

                    var uintX = *((UInt32*)(&posX));
                    var uintY = *((UInt32*)(&posY));
                    var uintZ = *((UInt32*)(&posZ));

                    // Add some randomness to prevent glitches when positions are the same (diagonal)
                    uintX ^= (uintX >> 19);
                    uintY ^= (uintY >> 8);

                    rngSeed = uintX ^ uintY ^ uintZ;
                }

                RandomSeedGenerator = new ParticleRandomSeedGenerator(rngSeed);
            }

        }

        /// <summary>
        /// Should be called before the other methods from <see cref="Update"/> to ensure the pool has sufficient capacity to handle all particles.
        /// </summary>
        private void EnsurePoolCapacity()
        {
            if (!Dirty)
                return;

            Dirty = false;

            if (MaxParticlesOverride > 0)
            {
                MaxParticles = MaxParticlesOverride;
                pool.SetCapacity(MaxParticles);
                PoolChangedNotification();
                return;
            }

            var particlesPerSecond = 0;

            foreach (var spawnerBase in Spawners)
            {
                particlesPerSecond += spawnerBase.GetMaxParticlesPerSecond();
            }

            MaxParticles = (int)Math.Ceiling(ParticleMaxLifetime * particlesPerSecond);

            pool.SetCapacity(MaxParticles);
            PoolChangedNotification();
        }

        /// <summary>
        /// Should be called before <see cref="ApplyParticleUpdaters"/> to ensure dead particles are removed before they are updated
        /// </summary>
        /// <param name="dt">Delta time, elapsed time since the last call, in seconds</param>
        private unsafe void MoveAndDeleteParticles(float dt)
        {
            // Hardcoded life update
            if (pool.FieldExists(ParticleFields.RemainingLife) && pool.FieldExists(ParticleFields.RandomSeed))
            {
                var lifeField = pool.GetField(ParticleFields.RemainingLife);
                var randField = pool.GetField(ParticleFields.RandomSeed);
                var lifeStep = ParticleMaxLifetime - ParticleMinLifetime;

                var particleEnumerator = pool.GetEnumerator();
                while (particleEnumerator.MoveNext())
                {
                    var particle = particleEnumerator.Current;

                    var randSeed = *(RandomSeed*)(particle[randField]);
                    var life = (float*)particle[lifeField];

                    if (*life > 1)
                        *life = 1;

                    var startingLife = ParticleMinLifetime + lifeStep * randSeed.GetFloat(0);

                    if (*life <= 0 || (*life -= (dt / startingLife)) <= 0)
                    {
                        particleEnumerator.RemoveCurrent(ref particle);
                    }
                }
            }

            // Hardcoded position and velocity update
            if (pool.FieldExists(ParticleFields.Position) && pool.FieldExists(ParticleFields.Velocity))
            {
                // should this be a separate module?
                // Position and velocity update only
                var posField = pool.GetField(ParticleFields.Position);
                var velField = pool.GetField(ParticleFields.Velocity);

                foreach (var particle in pool)
                {
                    var pos = ((Vector3*)particle[posField]);
                    var vel = ((Vector3*)particle[velField]);

                    *pos += *vel*dt;
                }
            }
        }

        /// <summary>
        /// Should be called before <see cref="SpawnNewParticles"/> to ensure new particles are not moved the frame they spawn
        /// </summary>
        /// <param name="dt">Delta time, elapsed time since the last call, in seconds</param>
        private void ApplyParticleUpdaters(float dt)
        {
            foreach (var updater in Updaters)
            {
                if (updater.Enabled)
                    updater.Update(dt, pool);
            }
        }

        /// <summary>
        /// Spawns new particles and in general should be one of the last methods to call from the <see cref="Update"/> method
        /// </summary>
        /// <param name="dt">Delta time, elapsed time since the last call, in seconds</param>
        private unsafe void SpawnNewParticles(float dt)
        {
            foreach (var spawnerBase in Spawners)
            {
                if (spawnerBase.Enabled)
                    spawnerBase.SpawnNew(dt, this);
            }

            var capacity = pool.ParticleCapacity;
            if (capacity <= 0)
            {
                particlesToSpawn = 0;
                return;
            }

            // Sometimes particles will be spawned when there is no available space
            // In such occasions we have to buffer them and spawn them when space becomes available
            particlesToSpawn = Math.Min(pool.AvailableParticles, particlesToSpawn);

            if (particlesToSpawn <= 0)
            {
                particlesToSpawn = 0;
                return;
            }

            var lifeField = pool.GetField(ParticleFields.RemainingLife);
            var randField = pool.GetField(ParticleFields.RandomSeed);

            var startIndex = pool.NextFreeIndex % capacity;

            for (var i = 0; i < particlesToSpawn; i++)
            {
                var particle = pool.AddParticle();

                var randSeed = RandomSeedGenerator.GetNextSeed();

                *((RandomSeed*)particle[randField]) = randSeed;

                *((float*)particle[lifeField]) = 1; // Start at 100% normalized lifetime                
            }

            var endIndex = pool.NextFreeIndex % capacity;

            if (startIndex == endIndex)
            {
                // All particles are spawned in the same frame so change indices to 0 .. MAX
                startIndex = 0;
                endIndex = capacity;
                capacity++; // Prevent looping
            }

            particlesToSpawn = 0;

            initialDefaultFields.Initialize(pool, startIndex, endIndex, capacity);

            foreach (var initializer in Initializers)
            {
                if (initializer.Enabled)
                    initializer.Initialize(pool, startIndex, endIndex, capacity);
            }
        }

        #endregion

        #region Fields
        private readonly Dictionary<ParticleFieldDescription, int> requiredFields;

        /// <summary>
        /// Updates the mandatory required variations depending on what particle fields are available
        /// </summary>
        private void UpdateDefalutEffectVariations()
        {
            // TODO Change the vertex builder here
        }

        /// <summary>
        /// Add a particle field required by some dependent module. If the module already exists in the pool, only its reference counter is increased.
        /// </summary>
        /// <param name="description"></param>
        private void AddRequiredField(ParticleFieldDescription description)
        {
            int fieldReferences;
            if (requiredFields.TryGetValue(description, out fieldReferences))
            {
                // Field already exists. Increase the reference counter by 1
                requiredFields[description] = fieldReferences + 1;
                return;
            }

            // Check if the pool doesn't already have too many fields
            if (requiredFields.Count >= ParticlePool.DefaultMaxFielsPerPool)
                return;

            if (!pool.FieldExists(description, forceCreate: true))
                return;

            requiredFields.Add(description, 1);

            UpdateDefalutEffectVariations();
        }

        /// <summary>
        /// Remove a particle field no longer required by a dependent module. It only gets removed from the pool if it reaches 0 reference counters.
        /// </summary>
        /// <param name="description"></param>
        private void RemoveRequiredField(ParticleFieldDescription description)
        {
            int fieldReferences;
            if (requiredFields.TryGetValue(description, out fieldReferences))
            {
                requiredFields[description] = fieldReferences - 1;

                // If this was not the last field, other Updaters are still using it so don't remove it from the pool
                if (fieldReferences > 1)
                    return;

                pool.RemoveField(description);

                requiredFields.Remove(description);

                UpdateDefalutEffectVariations();
            }

            // This line can be reached when a AddModule was unsuccessful and the required fields should be cleaned up
        }

        #endregion

        #region Rendering

        /// <summary>
        /// The <see cref="ShapeBuilderBase"/> expands all living particles to vertex buffers for rendering
        /// </summary>
        /// <userdoc>
        /// The shape defines how each particle is expanded when rendered (camera-facing billboards, oriented quads, ribbons, etc.)
        /// </userdoc>
        [DataMember(40)]
        [Display("Shape")]
        [NotNull]
        public ShapeBuilderBase ShapeBuilder { get; set; } = new ShapeBuilderBillboard();

        /// <summary>
        /// The <see cref="ParticleMaterial"/> may update the vertex buffer, and it also applies the <see cref="Effect"/> required for rendering
        /// </summary>
        /// <userdoc>
        /// Material defines what effects, textures, coloring and other techniques are used when rendering the particles.
        /// </userdoc>
        [DataMember(50)]
        [Display("Material")]
        [NotNull]
        public ParticleMaterial Material { get; set; } = new ParticleMaterialComputeColor();

        private ParticleVertexBuilder vertexBuilder = new ParticleVertexBuilder();

        /// <summary>
        /// <see cref="PrepareForDraw"/> prepares and updates the Material, ShapeBuilder and VertexBuilder if necessary
        /// </summary>
        private void PrepareForDraw()
        {
            Material.PrepareForDraw(vertexBuilder, ParticleSorter);

            ShapeBuilder.PrepareForDraw(vertexBuilder, ParticleSorter);

            // Update the vertex builder and the vertex layout if needed
            if (Material.VertexLayoutHasChanged || ShapeBuilder.VertexLayoutHasChanged)
            {
                vertexBuilder.ResetVertexElementList();

                Material.UpdateVertexBuilder(vertexBuilder);

                ShapeBuilder.UpdateVertexBuilder(vertexBuilder);

                vertexBuilder.UpdateVertexLayout();
            }
        }


        public void Draw(GraphicsDevice device, RenderContext context, ref Matrix viewMatrix, ref Matrix projMatrix, ref Matrix invViewMatrix, Color4 color)
        {
            Material.Setup(device, context, viewMatrix, projMatrix, color);
            Material.ApplyEffect(device);

            PrepareForDraw();

            // Get camera-space X and Y axes for billboards and sort the particles by depth
            var unitX = new Vector3(invViewMatrix.M11, invViewMatrix.M12, invViewMatrix.M13);
            var unitY = new Vector3(invViewMatrix.M21, invViewMatrix.M22, invViewMatrix.M23);
            depthSortVector = Vector3.Cross(unitX, unitY);
            ParticleSorter.Sort();

            // Local/World emitter
            var posIdentity = new Vector3(0, 0, 0);
            var rotIdentity = new Quaternion(0, 0, 0, 1);
            var scaleIdentity = 1f;
            if (simulationSpace == EmitterSimulationSpace.Local)
            {
                posIdentity   = drawPosition;
                rotIdentity   = drawRotation;
                scaleIdentity = drawScale;
            }

            vertexBuilder.SetRequiredQuads(ShapeBuilder.QuadsPerParticle, pool.LivingParticles, pool.ParticleCapacity);

            vertexBuilder.StartBuffer(device, Material.Effect);

            ShapeBuilder.BuildVertexBuffer(vertexBuilder, unitX, unitY, ref posIdentity, ref rotIdentity, scaleIdentity, ParticleSorter);

            vertexBuilder.RestartBuffer();

            Material.PatchVertexBuffer(vertexBuilder, unitX, unitY, ParticleSorter);

            //if (Material.GetInputSignature() != vertexBuilder.GetInputSignature())
            //{
            //    return;
            //}

            vertexBuilder.FlushBuffer(device);
        }

        public int GetRequiredQuadCount()
        {
            return ShapeBuilder.QuadsPerParticle * pool.LivingParticles;
        }
        #endregion

        #region Particles
        [DataMemberIgnore]
        private int particlesToSpawn = 0;

        /// <summary>
        /// Requests the emitter to spawn several new particles.
        /// The particles are buffered and will be spawned during the <see cref="Update"/> step
        /// </summary>
        /// <param name="count"></param>
        public void EmitParticles(int count)
        {
            particlesToSpawn += count;
        }

        [DataMemberIgnore]
        public bool Dirty { get; internal set; }

        private int maxParticlesOverride;
        /// <summary>
        /// Maximum particles (if positive) overrides the maximum particle count limitation
        /// </summary>
        /// <userdoc>
        /// Leave it 0 for unlimited (automatic) pool size. If positive, it limits the maximum number of living particles this Emitter can have at any given time.
        /// </userdoc>
        [DataMember(5)]
        [Display("Maximum particles")]
        public int MaxParticlesOverride
        {
            get { return maxParticlesOverride; }
            set
            {
                Dirty = true;
                maxParticlesOverride = value;
            }
        }

        [DataMemberIgnore]
        public int MaxParticles { get; private set; }

        private float particleMinLifetime = 1;

        /// <summary>
        /// Minimum particle lifetime, in seconds. Should be positive and no bigger than <see cref="ParticleMaxLifetime"/>
        /// </summary>
        /// <userdoc>
        /// When a new particle is born it will have at least that much Lifetime remaining (in seconds)
        /// </userdoc>
        [DataMember(8)]
        [Display("Particle's min lifetime")]
        public float ParticleMinLifetime
        {
            get { return particleMinLifetime; }
            set
            {
                if (value <= 0) //  || value > particleMaxLifetime - there is a problem with reading data when MaxLifetime is still not initialized
                    return;

                Dirty = true;
                particleMinLifetime = value;
            }
        }

        private float particleMaxLifetime = 1;

        /// <summary>
        /// Maximum particle lifetime, in seconds. Should be positive and no smaller than <see cref="ParticleMinLifetime"/>
        /// </summary>
        /// <userdoc>
        /// When a new particle is born it will have at most that much Lifetime remaining (in seconds)
        /// </userdoc>
        [DataMember(10)]
        [Display("Particle's max lifetime")]
        public float ParticleMaxLifetime
        {
            get { return particleMaxLifetime; }
            set
            {
                if (value < particleMinLifetime)
                    return;

                Dirty = true;
                particleMaxLifetime = value;
            }
        }

        private EmitterSimulationSpace simulationSpace = EmitterSimulationSpace.World;

        /// <summary>
        /// Simulation space defines if the particles should be born in world space, or local to the emitter
        /// </summary>
        /// <userdoc>
        /// World space particles persist in world space after they are born and do not automatically change with the Emitter. Local space particles persist in the Emitter's local space and follow it whenever the Emitter's locator changes.
        /// </userdoc>
        [DataMember(11)]
        [Display("Simulation Space")]
        public EmitterSimulationSpace SimulationSpace
        {
            get { return simulationSpace; }
            set
            {
                if (value == simulationSpace)
                    return;

                simulationSpace = value;

                SimulationSpaceChanged();
            }
        }

        private Vector3 drawPosition    = new Vector3(0, 0, 0);
        private Quaternion drawRotation = new Quaternion(0, 0, 0, 1);
        private float drawScale         = 1f;

        /// <summary>
        /// Changes the particle fields whenever the simulation space changes (World to Local or Local to World)
        /// This is a strictly debug feature so it (probably) won't be invoked during the game (unless changing the simulation space is intended?)
        /// </summary>
        private void SimulationSpaceChanged()
        {
            if (simulationSpace == EmitterSimulationSpace.Local)
            {
                // World -> Local

                var negativeTranslation = -drawPosition;
                var negativeScale = (drawScale > 0) ? 1f/drawScale : 1f;
                var negativeRotation = drawRotation;
                negativeRotation.Conjugate();

                if (pool.FieldExists(ParticleFields.Position))
                {
                    var posField = pool.GetField(ParticleFields.Position);

                    foreach (var particle in pool)
                    {
                        var position = particle.Get(posField);

                        position = position + negativeTranslation;
                        position = position * negativeScale;

                        negativeRotation.Rotate(ref position);

                        particle.Set(posField, position);
                    }
                }

                if (pool.FieldExists(ParticleFields.OldPosition))
                {
                    var posField = pool.GetField(ParticleFields.OldPosition);

                    foreach (var particle in pool)
                    {
                        var position = particle.Get(posField);

                        position = position + negativeTranslation;
                        position = position * negativeScale;

                        negativeRotation.Rotate(ref position);

                        particle.Set(posField, position);
                    }
                }

                if (pool.FieldExists(ParticleFields.Velocity))
                {
                    var velField = pool.GetField(ParticleFields.Velocity);

                    foreach (var particle in pool)
                    {
                        var velocity = particle.Get(velField);

                        velocity = velocity * negativeScale;

                        negativeRotation.Rotate(ref velocity);

                        particle.Set(velField, velocity);
                    }
                }

                if (pool.FieldExists(ParticleFields.Size))
                {
                    var sizeField = pool.GetField(ParticleFields.Size);

                    foreach (var particle in pool)
                    {
                        var size = particle.Get(sizeField);

                        size = size * negativeScale;

                        particle.Set(sizeField, size);
                    }
                }

                // TODO Rotation

            }
            else
            {
                // Local -> World

                if (pool.FieldExists(ParticleFields.Position))
                {
                    var posField = pool.GetField(ParticleFields.Position);

                    foreach (var particle in pool)
                    {
                        var position = particle.Get(posField);

                        drawRotation.Rotate( ref position );

                        position = position * drawScale + drawPosition;

                        particle.Set(posField, position);
                    }
                }

                if (pool.FieldExists(ParticleFields.OldPosition))
                {
                    var posField = pool.GetField(ParticleFields.OldPosition);

                    foreach (var particle in pool)
                    {
                        var position = particle.Get(posField);

                        drawRotation.Rotate(ref position);

                        position = position * drawScale + drawPosition;

                        particle.Set(posField, position);
                    }
                }

                if (pool.FieldExists(ParticleFields.Velocity))
                {
                    var velField = pool.GetField(ParticleFields.Velocity);

                    foreach (var particle in pool)
                    {
                        var velocity = particle.Get(velField);

                        drawRotation.Rotate(ref velocity);

                        velocity = velocity * drawScale;

                        particle.Set(velField, velocity);
                    }
                }

                if (pool.FieldExists(ParticleFields.Size))
                {
                    var sizeField = pool.GetField(ParticleFields.Size);

                    foreach (var particle in pool)
                    {
                        var size = particle.Get(sizeField);

                        size = size * drawScale;

                        particle.Set(sizeField, size);
                    }
                }

                // TODO Rotation

            }
        }

        private EmitterRandomSeedMethod randomSeedMethod = EmitterRandomSeedMethod.Time;

        /// <summary>
        /// Random numbers in the <see cref="ParticleSystem"/> are generated based on a seed, which in turn can be generated using several methods.
        /// </summary>
        /// <userdoc>
        /// All random numbers in the Particle System are based on a seed. If you use deterministic seeds, your particles will always behave the same way every time you start the simulation.
        /// </userdoc>
        [DataMember(12)]
        [Display("Random seed base")]
        public EmitterRandomSeedMethod RandomSeedMethod
        {
            get
            {
                return randomSeedMethod;
            }

            set
            {
                randomSeedMethod = value;
                delayInit = false;
            }
        }

        #endregion

    }
}
