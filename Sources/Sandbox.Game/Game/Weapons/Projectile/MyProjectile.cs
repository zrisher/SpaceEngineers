using System;
using System.Diagnostics;
using Sandbox.Common;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Utils;
using Sandbox.Game.World;
using VRage.Utils;
using VRageMath;

using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.EnvironmentItems;
using Sandbox.Game.Multiplayer;
using Sandbox.Common.ObjectBuilders.Definitions;
using VRage;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Library.Utils;
using VRage.ModAPI;
using VRage.Game.Components;
using System.Collections.Generic;
using VRage.Game.Models;
using VRage.Game.Entity;
using VRage.Game;

namespace Sandbox.Game.Weapons
{

    public delegate void MyCustomHitParticlesMethod(ref Vector3D hitPoint, ref Vector3 normal, ref Vector3D direction, IMyEntity entity, MyEntity weapon, float scale, MyEntity ownerEntity = null);
    public delegate void MyCustomHitMaterialMethod(ref Vector3D hitPoint, ref Vector3 normal, ref Vector3D direction, IMyEntity entity, MySurfaceImpactEnum surfaceImpact, MyEntity weapon, float scale);

    public enum MySurfaceImpactEnum
    {
        METAL,
        DESTRUCTIBLE,
        INDESTRUCTIBLE,
        CHARACTER
    }

    // One projectile eats 700 B of memory (316 directly, rest in members)
    // Imho quite a lot
    class MyProjectile
    {
        //  DEFLECTED projectiles live a little while before being KILLED.
        //  Projectiles are killed in two states. First we get collision/timeout in update, but still need to draw
        //  trail polyline, so we can't remove it from buffer. Second state is after 'killed' projectile is drawn
        //  and only then we remove it from buffer.
        enum MyProjectileStateEnum : byte
        {
            ACTIVE,
            DEFLECTED,
            KILLED,
            KILLED_AND_DRAWN
        }

        internal struct HitInfo
        {
            public IMyEntity Entity;
            public Vector3D Position;
            public Vector3 Normal;
            public bool Headshot;
        }

        const bool DEBUG_DRAW_PROJECTILES = false;
        const int CHECK_INTERSECTION_INTERVAL = 5; //projectile will check for intersection each n-th frame with n*longer line
        const int DEBUG_DRAW_EXTRA_FRAMES = 60 * 20; // How long to keep debug trails around
        const int DEFLECTED_MAX_FRAMES = 75; // How long to wait before killing deflected projectiles
        const float DEFAULT_SURFACE_ELASTICITY = .10f; // How far a block's surface will stretch before giving way
        const float DEFAULT_TRAIL_LENGTH = 40; // Multiplied by ammo definition's trail factor
        const float DEFLECTED_SPEED_FACTOR = 0.5f; // Lose about 70% of energy, sqrt(.5) ~= .7

        //  IMPORTANT: This class isn't realy inicialized by constructor, but by Start()
        //  So don't initialize members here, do it in Start()

        MyProjectileStateEnum m_state;
        Vector3D m_origin;
        Vector3D m_velocity;
        Vector3D m_directionNormalized;
        float m_speed;
        float m_maxTrajectory;

        Vector3D m_position;
        MyEntity m_ignoreEntity;
        MyEntity m_weapon;

        bool m_drawTrail;
        int m_debugDrawExtraFrames;
        int m_framesSinceDeflected;
        int m_mass;
        double m_distanceTraveled;
        double m_knownForwardClearance;
        HitInfo? m_nextHit;
        List<HitInfo> m_hits;

        //  Type of this projectile
        MyProjectileAmmoDefinition m_projectileAmmoDefinition;

        public MyEntity OwnerEntity = null;//rifle, block, ...
        public MyEntity OwnerEntityAbsolute = null;//character, main ship cockpit, ...

        private VRage.Game.Models.MyIntersectionResultLineTriangleEx? m_intersection = null;
        private List<MyLineSegmentOverlapResult<MyEntity>> m_entityRaycastResult = null;

        public MyProjectile()
        {
        }

        //  This method realy initiates/starts the missile
        //  IMPORTANT: Direction vector must be normalized!
        //  ALSO IMPORTANT: Every single instance variable MUST be reset here, because projectiles are recycled.
        //   A KILLED_AND_DRAWN projectile will be restarted with new info.
        //   You CANNOT rely on default values.
        // Projectile count multiplier - when real rate of fire it 45, but we shoot only 10 projectiles as optimization count multiplier will be 4.5
        public void Start(MyProjectileAmmoDefinition ammoDefinition, MyEntity ignoreEntity, Vector3D origin, Vector3 initialVelocity, Vector3 directionNormalized, MyEntity weapon)
        {
            VRageRender.MyRenderProxy.GetRenderProfiler().StartProfilingBlock("Projectile.Start");

            m_projectileAmmoDefinition = ammoDefinition;
            m_state = MyProjectileStateEnum.ACTIVE;
            m_ignoreEntity = ignoreEntity;
            m_origin = origin + 0.1 * (Vector3D)directionNormalized;
            m_position = m_origin;
            m_weapon = weapon;

            if (ammoDefinition.ProjectileTrailProbability >= MyUtils.GetRandomFloat(0, 1))
                m_drawTrail = true;
            else
                m_drawTrail = false;

                        /*
            if (MyConstants.EnableAimCorrection)
            {
                if (m_ammoProperties.AllowAimCorrection) // Autoaim only available for player
                {
                    VRageRender.MyRenderProxy.GetRenderProfiler().StartProfilingBlock("Projectile.Start autoaim generic");
                    //Intersection ignores children of "ignoreEntity", thus we must not hit our own barrels
                    correctedDirection = MyEntities.GetDirectionFromStartPointToHitPointOfNearestObject(ignoreEntity, m_weapon, origin, m_ammoProperties.MaxTrajectory);
                    VRageRender.MyRenderProxy.GetRenderProfiler().EndProfilingBlock();
                }
            }             */

            m_directionNormalized = directionNormalized;
            m_speed = ammoDefinition.DesiredSpeed * (ammoDefinition.SpeedVar > 0.0f ? MyUtils.GetRandomFloat(1 - ammoDefinition.SpeedVar, 1 + ammoDefinition.SpeedVar) : 1.0f);
            m_velocity = initialVelocity + m_directionNormalized * m_speed; ;
            m_maxTrajectory = ammoDefinition.MaxTrajectory * MyUtils.GetRandomFloat(0.8f, 1.2f); // +/- 20%
            m_mass = (int)(2 * m_projectileAmmoDefinition.ProjectileMassDamage / (ammoDefinition.DesiredSpeed * ammoDefinition.DesiredSpeed));

            m_distanceTraveled = m_knownForwardClearance = 0;
            m_framesSinceDeflected = m_debugDrawExtraFrames = 0;
            m_nextHit = null;
            if (m_hits == null)
                m_hits = new List<HitInfo>();
            else
                m_hits.Clear();

            // prefetch planet voxels in our path
            LineD line = new LineD(m_origin, m_origin + m_directionNormalized * m_maxTrajectory);

            if (m_entityRaycastResult == null)
            {
                m_entityRaycastResult = new List<MyLineSegmentOverlapResult<MyEntity>>(16);
            }
            else
            {
                m_entityRaycastResult.Clear();
            }
            MyGamePruningStructure.GetAllEntitiesInRay(ref line, m_entityRaycastResult, MyEntityQueryType.Static);

            foreach (var entity in m_entityRaycastResult)
            {
                MyVoxelPhysics planetPhysics = entity.Element as MyVoxelPhysics;
                if (planetPhysics != null)
                {
                    planetPhysics.PrefetchShapeOnRay(ref line);
                }
            }

            VRageRender.MyRenderProxy.GetRenderProfiler().EndProfilingBlock();
        }
        
        //  Update position, check collisions, etc.
        //  Return false if projectile dies/timeouts in this tick.
        public bool Update()
        {
            // Projectile was deflected, kill after a few seconds
            if (m_state == MyProjectileStateEnum.DEFLECTED)
            {
                m_framesSinceDeflected++;
                if (m_framesSinceDeflected >= DEFLECTED_MAX_FRAMES)
                    m_state = MyProjectileStateEnum.KILLED;
            }

            //  Projectile was killed , but still not last time drawn, so we don't need to do update (we are waiting for last draw)
            if (m_state == MyProjectileStateEnum.KILLED)
                return true;

            //  Projectile was killed and last time drawn, so we can finally remove it from buffer
            if (m_state == MyProjectileStateEnum.KILLED_AND_DRAWN)
            {
                StopEffect();
                return false;
            }

            //  Distance timeout
            if (m_distanceTraveled >= m_maxTrajectory)
            {
                StopEffect();
                m_state = MyProjectileStateEnum.KILLED;
                return true;
            }

            // Simulate travel and hits
            ProfilerShort.Begin("Projectile.Update");
            UpdateTravel();
            ProfilerShort.End();
            return true;
        }

        #region Entity Helpers

        /// <summary>
        /// Get hit details for the first non-ignored entity in the projectile's path
        /// </summary>
        /// <remarks>
        /// We do two types of tests - 1) a physics raycast and 2) an entities line overlap test.
        /// 1 is cheaper because it uses a pruning structure, but apparently it misses characters and hitboxes are all prisms.
        /// 2 takes longer but provides model-level collision, so hit points are much more accurate on things that aren't perfect rectangular prisms.
        /// Unfortunately, 2 also tends to provide the wrong hit normals for under-construction blocks and misses (more) of closed airtight doors.
        /// So we use 1 unless we find a character in the path with 2.
        /// </remarks>
        private void GetHitEntityAndPosition(LineD line, out IMyEntity entity, out Vector3D hitPosition, out Vector3 hitNormal, out bool hitHead)
        {
            entity = null;
            hitPosition = hitNormal = Vector3.Zero;
            hitHead = false;

            VRageRender.MyRenderProxy.GetRenderProfiler().StartProfilingBlock("MyEntities.GetIntersectionWithLine()");
            //m_intersection = MyEntities.GetIntersectionWithLine(ref line, m_ignoreEntity, m_weapon, false, false, true, IntersectionFlags.ALL_TRIANGLES, VRage.Game.MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS * CHECK_INTERSECTION_INTERVAL);
            m_intersection = null;
            VRageRender.MyRenderProxy.GetRenderProfiler().EndProfilingBlock();

            if (m_intersection != null) 
            { 
                // will never trigger, see commented code above ^
                entity = m_intersection.Value.Entity;
                hitPosition = m_intersection.Value.IntersectionPointInWorldSpace;
                hitNormal = m_intersection.Value.NormalInWorldSpace;
            }
            // 1. rough raycast
            if (entity == null)
            {
                ProfilerShort.Begin("MyGamePruningStructure::CastProjectileRay");
                MyPhysics.HitInfo? hitInfo = MyPhysics.CastRay(line.From, line.To, MyPhysics.CollisionLayers.DefaultCollisionLayer);
                //MyPhysics.HitInfo? hitInfo = null;
                //if (Sandbox.Game.Gui.MyMichalDebugInputComponent.Static.CastLongRay)
                //    hitInfo = MyPhysics.CastLongRay(line.From, line.To);
                //else
                //    hitInfo = MyPhysics.CastRay(line.From, line.To, MyPhysics.CollisionLayers.DefaultCollisionLayer);
                ProfilerShort.End();
                if (hitInfo.HasValue)
                {
                    entity = hitInfo.Value.HkHitInfo.GetHitEntity() as MyEntity;
                    hitPosition = hitInfo.Value.Position;
                    hitNormal = hitInfo.Value.HkHitInfo.Normal;

                    if (IsIgnoredEntity(entity, hitNormal))
                    {
                        entity = null;
                        hitPosition = hitNormal = Vector3.Zero;
                    }
                }
            }

            // 2. prevent shooting through characters, retest trajectory between entity and player
            if (!(entity is MyCharacter) || entity == null)
            {
                // first: raycast, get all entities in line, limit distance if possible
                LineD lineLimited = new LineD(line.From, entity == null ? line.To : hitPosition);
                if (m_entityRaycastResult == null)
                {
                    m_entityRaycastResult = new List<MyLineSegmentOverlapResult<MyEntity>>(16);
                }
                else
                {
                    m_entityRaycastResult.Clear();
                }
                MyGamePruningStructure.GetAllEntitiesInRay(ref lineLimited, m_entityRaycastResult);
                // second: precise tests, find best result
                double bestDistanceSq = double.MaxValue;
                IMyEntity entityBest = null;
                for (int i = 0; i < m_entityRaycastResult.Count; i++)
                {
                    if (m_entityRaycastResult[i].Element is MyCharacter)
                    {
                        MyCharacter hitCharacter = m_entityRaycastResult[i].Element as MyCharacter;
                        VRage.Game.Models.MyIntersectionResultLineTriangleEx? t;
                        hitCharacter.GetIntersectionWithLine(ref line, out t, out hitHead);

                        if (t != null && !IsIgnoredEntity(entity, t.Value.NormalInWorldSpace))
                        {
                            double distanceSq = Vector3D.DistanceSquared(t.Value.IntersectionPointInWorldSpace, line.From);
                            if (distanceSq < bestDistanceSq)
                            {
                                bestDistanceSq = distanceSq;
                                entityBest = hitCharacter;
                                hitPosition = t.Value.IntersectionPointInWorldSpace;
                                hitNormal = t.Value.NormalInWorldSpace;
                            }
                        }
                    }
                }
                // finally: do we have best result? then return it
                if (entityBest != null)
                {
                    entity = entityBest; 
                    return; // this was precise result, so return
                }
            }

            // 3. nothing found in the precise test? then fallback to already found results
            if (entity == null)
                return; // no fallback results

            if (entity is MyCharacter) // retest character found in fallback
            {
                MyCharacter hitCharacter = entity as MyCharacter;
                VRage.Game.Models.MyIntersectionResultLineTriangleEx? t;
                hitCharacter.GetIntersectionWithLine(ref line, out t, out hitHead);
                if (t == null || IsIgnoredEntity(entity, t.Value.NormalInWorldSpace))
                {
                    entity = null; // no hit.
                }
                else
                {
                    hitPosition = t.Value.IntersectionPointInWorldSpace;
                    hitNormal = t.Value.NormalInWorldSpace;
                    hitHead = hitHead && m_projectileAmmoDefinition.HeadShot; // allow head shots only for ammo supporting it in definition
                }
            }
            else
            {
                //entity = entity.GetTopMostParent();
            }
        }

        private void DoDamage(float damage, Vector3D hitPosition, IMyDestroyableObject destroyable)
        {
            if (destroyable == null) return;

            //damage tracking
            MyEntity ent = (MyEntity)MySession.Static.ControlledEntity;
            if (this.OwnerEntityAbsolute != null && this.OwnerEntityAbsolute.Equals(MySession.Static.ControlledEntity))
            {
                MySession.Static.TotalDamageDealt += (uint)damage;
            }

            if (!Sync.IsServer)
                return;

            // Determine damage attributes depending on hit object
            long attackerId = m_weapon != null ? GetSubpartOwner(m_weapon).EntityId : 0;
            MyStringHash damageType = MyDamageType.Bullet;
            MyCubeGrid gridToDeform = null;

            if (destroyable is MyCharacter)
            {
                if (m_projectileAmmoDefinition.ProjectileType == MyProjectileType.Bolt)
                    damageType = MyDamageType.Bolt;
            }
            else if (destroyable is MySlimBlock)
            {
                gridToDeform = ((MySlimBlock)destroyable).CubeGrid;
            }

            // Do Damage
            destroyable.DoDamage(damage, damageType, true, attackerId: attackerId);

            if (gridToDeform != null)
                ApplyDeformationCubeGrid(hitPosition, gridToDeform, damage);


            //Handle damage ?? some WIP code by Ondrej
            //MyEntity damagedObject = entity;
            //damagedObject.DoDamage(m_ammoProperties.HealthDamage, m_ammoProperties.ShipDamage, m_ammoProperties.EMPDamage, m_ammoProperties.DamageType, m_ammoProperties.AmmoType, m_ignorePhysObject);
            //if (MyMultiplayerGameplay.IsRunning)
            //    MyMultiplayerGameplay.Static.ProjectileHit(damagedObject, intersectionValue.IntersectionPointInWorldSpace, this.m_directionNormalized, MyAmmoConstants.FindAmmo(m_ammoProperties), this.OwnerEntity);

        }

        /// <summary>
        /// Tells us if an entity is to be ignored for collision checking.
        /// </summary>
        /// <remarks>
        /// We ignore everything without physics, the gun that shot the projectile,
        /// and entities with a positive hit normal (i.e. we're hitting it from "inside" the entity)
        /// </summary>
        private bool IsIgnoredEntity(IMyEntity entity, Vector3 hitNormal)
        {
            return entity == null || entity.Physics == null || entity == m_ignoreEntity ||
                ((m_ignoreEntity is IMyGunBaseUser) &&
                    (m_ignoreEntity as IMyGunBaseUser).Owner is MyCharacter &&
                    (m_ignoreEntity as IMyGunBaseUser).Owner == entity) ||
                (Vector3D.Dot(hitNormal, m_directionNormalized) > 0);
        }

        /// <summary>
        /// Find the destroyable object at hitPosition on damagedEntity
        /// </summary>
        private IMyDestroyableObject GetDestroyableObject(Vector3D hitPosition, IMyEntity damagedEntity)
        {
            // If it's already destroyable, we're set!
            if (damagedEntity is IMyDestroyableObject)
                return damagedEntity as IMyDestroyableObject;

            // If it's a cubegrid, find the block at hitposition
            if (damagedEntity is MyCubeGrid)
            {
                var grid = damagedEntity as MyCubeGrid;
                if (grid.Physics != null && grid.Physics.Enabled && grid.BlocksDestructionEnabled)
                {
                    Vector3I blockPos;
                    grid.FixTargetCube(out blockPos, Vector3D.Transform(hitPosition, grid.PositionComp.WorldMatrixNormalizedInv) / grid.GridSize);
                    var block = grid.GetCubeBlock(blockPos);

                    if (block != null)
                        return block;
                }

                return null;
            }

            //By Gregory: When MyEntitySubpart (e.g. extended parts of pistons and doors) damage the whole parent component
            //Temporary fix! Maybe other solution? MyEntitySubpart cannot implement IMyDestroyableObject cause is on dependent namespace
            if (damagedEntity is MyEntitySubpart && damagedEntity.Parent != null && damagedEntity.Parent.Parent is MyCubeGrid)
                return GetDestroyableObject(damagedEntity.Parent.WorldAABB.Center, damagedEntity.Parent.Parent);

            return null;
        }


        private MyEntity GetSubpartOwner(MyEntity entity)
        {
            if (entity == null)
                return null;

            if (!(entity is MyEntitySubpart))
                return entity;

            MyEntity result = entity;
            while (result is MyEntitySubpart && result != null)
                result = result.Parent;

            if (result == null)
                return entity;
            else
                return result;
        }

        #endregion
        #region Ballistic Trajectory Simulation

        /// <summary>
        /// Calculate clearance for the next CHECK_INTERSECTION_INTERVAL frames
        /// </summary>
        /// <remarks>
        /// This will fail to find an entity that moves into the projectile's trajectory between checks.
        /// But with a small CHECK_INTERSECTION_INTERVAL it will appear as a believable near miss.
        /// </remarks>
        private void UpdateClearance()
        {
            Vector3 end = m_position + CHECK_INTERSECTION_INTERVAL * (m_velocity * VRage.Game.MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS);
            LineD line = new LineD(m_position, end);

            IMyEntity hitEntity;
            Vector3D hitPosition;
            Vector3 hitNormal;
            bool hitHeadshot;

            GetHitEntityAndPosition(line, out hitEntity, out hitPosition, out hitNormal, out hitHeadshot);

            if (hitEntity != null)
            {
                m_nextHit = new HitInfo() { Entity = hitEntity, Position = hitPosition, Normal = hitNormal, Headshot = hitHeadshot };
                m_knownForwardClearance = (hitPosition - m_position).Length();
            }
            else
            {
                m_nextHit = null;
                m_knownForwardClearance = line.Length;
            }
        }

        /// <summary>
        /// Simulate travel
        /// </summary>
        /// <remarks>
        /// When we hit something, we wait a frame to move again so the game can update block destruction in our path.
        /// Technically this slows the projectile, but it should be imperceptible to the player.
        /// </remarks>
        private void UpdateTravel()
        {
            double desiredTravelDistance = m_speed * VRage.Game.MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;

            // If we're out of known clearance but we aren't about to hit something, update clearance
            if (m_knownForwardClearance < desiredTravelDistance && m_nextHit == null)
                UpdateClearance();

            // If we have enough space ahead to move a full frame, do so and return
            if (m_knownForwardClearance >= desiredTravelDistance)
            {
                m_position += m_directionNormalized * desiredTravelDistance;
                m_distanceTraveled += desiredTravelDistance;
                m_knownForwardClearance -= desiredTravelDistance;
                return;
            }

            // Otherwise, move forward the space we can
            if (m_knownForwardClearance > 0)
            {
                m_position += m_directionNormalized * m_knownForwardClearance;
                m_distanceTraveled += m_knownForwardClearance;
                m_knownForwardClearance = 0;
            }

            // And deal with what's in our way - do the hit and update velocity
            if (m_nextHit == null || m_nextHit.Value.Entity == null)
            {
                Debug.Assert(false, "Projectile next hit should exist when updated clearance < desired distance.");
                return;
            }
            m_velocity = DoBallisticInteraction(m_nextHit.Value);
            Debug.Assert(m_velocity.Length() <= m_speed, "Projectile speed should not be increased by ballistic interation.");

            m_directionNormalized = Vector3D.Normalize(m_velocity);
            m_speed = (float)m_velocity.Length();
            m_hits.Insert(0, m_nextHit.Value);
            m_nextHit = null;
        }

        /// <summary>
        /// Do visual/sound effects for hits, apply damage and impulse on hit entity, and return new velocity
        /// </summary>
        private Vector3D DoBallisticInteraction(HitInfo hit)
        {
            if (hit.Entity == null)
            {
                Debug.Assert(false, "Projectile ballistic interaction should receive non-null hit entity.");
                return Vector3D.Zero;
            }

            // Interrupt shooting characters
            MyCharacter hitCharacter = hit.Entity as MyCharacter;
            if (hitCharacter != null)
            {
                IStoppableAttackingTool stoppableTool = hitCharacter.CurrentWeapon as IStoppableAttackingTool;
                if (stoppableTool != null)
                    stoppableTool.StopShooting(OwnerEntity);
            }

            // Get material properties for effects
            MySurfaceImpactEnum surfaceImpact;
            MyStringHash materialType;
            GetSurfaceAndMaterial(hit.Entity, ref  hit.Position, out surfaceImpact, out materialType);

            // Sound effect
            PlayHitSound(materialType, hit.Entity, hit.Position);

            // Visual effects - Create smoke and debris particle at the place of voxel/model hit
            if (surfaceImpact == MySurfaceImpactEnum.CHARACTER && hit.Entity is MyCharacter)
            {
                MyStringHash bullet = MyStringHash.GetOrCompute("RifleBullet");//temporary
                MyMaterialPropertiesHelper.Static.TryCreateCollisionEffect(
                                    MyMaterialPropertiesHelper.CollisionType.Start,
                                    hit.Position,
                                    hit.Normal,
                                    bullet, materialType);
            }
            else
            {
                m_projectileAmmoDefinition.ProjectileOnHitParticles(ref hit.Position, ref hit.Normal, ref m_directionNormalized, hit.Entity, m_weapon, 1, OwnerEntity);
            }

            Vector3D particleHitPosition = hit.Position + m_directionNormalized * -0.2;
            m_projectileAmmoDefinition.ProjectileOnHitMaterialParticles(ref particleHitPosition, ref hit.Normal, ref m_directionNormalized, hit.Entity, surfaceImpact, m_weapon, 1);

            CreateDecal(materialType);

            // Damage and impulse effects
            float damage;
            float impulse;
            Vector3D newVelocity;

            // Note that checking for the destroyable here gets us the most up-to-date target if hitting a cubegrid
            IMyDestroyableObject destroyable = GetDestroyableObject(hit.Position, hit.Entity);

            if (destroyable != null)
            {
                TryPenetrate(hit.Normal, destroyable, m_velocity, m_mass, out damage, out impulse, out newVelocity);

                if (damage > 0)
                {
                    if (hit.Headshot)
                        damage *= m_projectileAmmoDefinition.ProjectileHeadShotDamage / m_projectileAmmoDefinition.ProjectileMassDamage;

                    DoDamage(damage, hit.Position, destroyable);
                }

                if (impulse > 0 && (m_weapon == null || m_weapon.GetTopMostParent() != hit.Entity.GetTopMostParent()))
                        ApplyProjectileForce(hit.Entity, hit.Position, hit.Normal, false, impulse);
            }
            else
            {
                // We've hit an indestructible object, stop here.
                if (hit.Entity is MyVoxelMap)
                {
                    // TODO: Damage voxels with projectiles
                    // We could remove a portion of the voxel according to its density, but it's expensive. 
                    // We could only do it given a certain weapon rate of fire and burst rate, though.
                }
                else
                {
                    Debug.Assert(false, "Projectile hit an unknown destroyable object: " + hit.Entity.ToString());
                }

                newVelocity = Vector3D.Zero;
            }

            return newVelocity;
        }

        /// <summary>
        /// Determines the interaction between a projectile and a hit entity
        /// Gives us the damage and impulse done to the object and the new velocity of the projectile
        /// </summary>
        private void TryPenetrate(Vector3 hitNormal, IMyDestroyableObject hitEntity, Vector3D initialVelocity, float mass,
            out float damage, out float impulse, out Vector3D newVelocity)
        {

            damage = 0;
            impulse = 0;

            Vector3D initialDirection = Vector3D.Normalize(initialVelocity);
            float initialSpeed = (float)initialVelocity.Length();
            float initialEnergy = .5f * mass * initialSpeed * initialSpeed;

            // === Calculate the counter-force applied to projectile by entity

            // find energy applied by projectile to entity against the surface normal
            double normalRatio;
            Vector3D.Dot(ref initialDirection, ref hitNormal, out normalRatio);
            if (normalRatio > 0)
            {
                Debug.Assert(false, "Projectile hit normal ratio should be <= 0.");
                normalRatio *= -1;
            }
            Vector3D velocityAgainstSurfaceNormal = initialSpeed * normalRatio * (Vector3D)hitNormal;
            float energyAgainstSurfaceNormal = initialEnergy * -1 * (float)normalRatio;

            // find the energy applied to projectile by entity along surface normal
            float entityIntegrity = hitEntity.Integrity;
            if (hitEntity is MySlimBlock)
                entityIntegrity /= ((MySlimBlock)hitEntity).DamageRatio * ((MySlimBlock)hitEntity).DeformationRatio;
            float potentialDeflectionEnergy = entityIntegrity * DEFAULT_SURFACE_ELASTICITY * hitEntity.ProjectileResistance / m_projectileAmmoDefinition.ProjectilePenetration;
            float deflectionEnergy = Math.Min(energyAgainstSurfaceNormal, potentialDeflectionEnergy);

            // === Affect the projectile and object accordingly
            newVelocity = initialVelocity;

            // if object provides enough resistance, deflect the projectile
            if (energyAgainstSurfaceNormal <= deflectionEnergy)
            {
                // There are a lot of ways we could derive reflection index and energy absorption; these are rough approximations
                newVelocity += (2 - deflectionEnergy / potentialDeflectionEnergy) * -1 * velocityAgainstSurfaceNormal;
                newVelocity *= DEFLECTED_SPEED_FACTOR;
                m_state = MyProjectileStateEnum.DEFLECTED; // flag for removal
            }
            else {
                // remove the energy expended overcoming deflection
                float energyRemaining = Math.Max(0, initialEnergy - deflectionEnergy);

                // if object provides enough integrity to stop projectile
                if (energyRemaining <= entityIntegrity)
                {
                    // stop it and convert remaining energy to damage
                    newVelocity = Vector3D.Zero;
                    damage = energyRemaining;
                }
                // otherwise, we're penetrating
                else
                {
                    // remove the energy expended passing through the block
                    damage = entityIntegrity;
                    energyRemaining -= entityIntegrity;

                    // apply defraction, sin (1) / sin(2) = n2 / n1
                    double speedRatio = Math.Sqrt(energyRemaining / initialEnergy);
                    Debug.Assert(0 <= speedRatio && speedRatio <= 1, "Projectile speedRatio of " + speedRatio.ToString() + " should be between 0 and 1.");
                    Vector3D newDirection = initialDirection * speedRatio - (Vector3D)hitNormal * (1 - speedRatio);
                    newDirection = Vector3D.Normalize(newDirection); // since hitNormal has less precision the result is often just a little short
                    newVelocity = newDirection * initialSpeed * speedRatio;
                }

            }

            // calculate impulse provided to the hit object
            impulse = mass * (float)(initialVelocity - newVelocity).Length();
        }

        #endregion
        #region Draw

        public void Draw()
        {
            if (m_state == MyProjectileStateEnum.KILLED)
            {
                m_debugDrawExtraFrames++;
                if (!DEBUG_DRAW_PROJECTILES || m_debugDrawExtraFrames >= DEBUG_DRAW_EXTRA_FRAMES)
                    m_state = MyProjectileStateEnum.KILLED_AND_DRAWN;
            }

            if (m_drawTrail) DrawTrail();
            if (DEBUG_DRAW_PROJECTILES) DebugDraw();
        }

        private void DrawTrail()
        {
            double desiredTrailLength = DEFAULT_TRAIL_LENGTH *
                m_projectileAmmoDefinition.ProjectileTrailScale *
                m_speed / m_projectileAmmoDefinition.DesiredSpeed *
                MyUtils.GetRandomFloat(0.6f, 0.8f);

            float color = MyUtils.GetRandomFloat(1, 2);
            float thickness = MyUtils.GetRandomFloat(0.2f, 0.3f) * m_projectileAmmoDefinition.ProjectileTrailScale;

            // Line particles (polyline) don't look good in distance. Start and end aren't rounded anymore and they just
            // look like a pieces of paper. Especially when zoomed-in.
            thickness *= MathHelper.Lerp(0.2f, 0.8f, MySector.MainCamera.Zoom.GetZoomLevel());

            double remainingDrawLength = desiredTrailLength;
            Vector3D lastTrailPoint = m_position;

            foreach (HitInfo hit in m_hits)
            {
                if (remainingDrawLength <= 0) break;

                DrawTrailSegment(lastTrailPoint, hit.Position, color, thickness, ref remainingDrawLength);
                lastTrailPoint = hit.Position;
            }

            if (remainingDrawLength > 0)
                DrawTrailSegment(lastTrailPoint, m_origin, color, thickness, ref remainingDrawLength);
        }

        /// <summary>
        /// Draw the projectile trail from one point to another.
        /// Limited to trailLengthRemaining, which is adjusted after drawing
        /// </summary>
        public void DrawTrailSegment(Vector3D from, Vector3D to, float color, float thickness, ref double trailLengthRemaining)
        {
            double length = Vector3D.Distance(from, to);
            Vector3D direction = Vector3D.Normalize(to - from);
            if (length > trailLengthRemaining) length = trailLengthRemaining;

            if (length > 0)
            {
                if (m_projectileAmmoDefinition.ProjectileTrailMaterial != null)
                {
                    MyTransparentGeometry.AddLineBillboard(
                        m_projectileAmmoDefinition.ProjectileTrailMaterial,
                        new Vector4(m_projectileAmmoDefinition.ProjectileTrailColor, 1),
                        from, direction, (float)length, thickness);
                }
                else
                {
                    MyTransparentGeometry.AddLineBillboard("ProjectileTrailLine",
                        new Vector4(m_projectileAmmoDefinition.ProjectileTrailColor * color, 1),
                        from, direction, (float)length, thickness);
                }

                trailLengthRemaining -= length;
            }
        }

        /// <summary>
        /// Debug draw the projectile's path, its hits, and the hit normals
        /// </summary>
        private void DebugDraw()
        {
            Vector3D lastPoint = m_position;
            foreach (HitInfo hit in m_hits)
            {
                VRageRender.MyRenderProxy.DebugDrawLine3D(lastPoint, hit.Position, Color.Red, Color.Green, false);
                VRageRender.MyRenderProxy.DebugDrawSphere(hit.Position, .1f, Color.Red, .8f, false);
                VRageRender.MyRenderProxy.DebugDrawLine3D(hit.Position, hit.Position + hit.Normal, Color.Orange, Color.Orange, false);
                lastPoint = hit.Position;
            }

            VRageRender.MyRenderProxy.DebugDrawLine3D(lastPoint, m_origin, Color.Red, Color.Green, false);
        }

        #endregion
        #region Hit Effects

        private static void GetSurfaceAndMaterial(IMyEntity entity, ref Vector3D hitPosition, out MySurfaceImpactEnum surfaceImpact, out MyStringHash materialType)
        {
            var voxelBase = entity as MyVoxelBase;
            if (voxelBase != null)
            {
                materialType = MyMaterialType.ROCK;
                surfaceImpact = MySurfaceImpactEnum.DESTRUCTIBLE;

                var voxelDefinition = voxelBase.GetMaterialAt(ref hitPosition);
                if(voxelDefinition != null)
                    materialType = MyStringHash.GetOrCompute(voxelDefinition.MaterialTypeName);
            }
            else if (entity is MyCharacter)
            {
                surfaceImpact = MySurfaceImpactEnum.CHARACTER;
                materialType = MyMaterialType.CHARACTER;
                if ((entity as MyCharacter).Definition.PhysicalMaterial != null) materialType = MyStringHash.GetOrCompute((entity as MyCharacter).Definition.PhysicalMaterial);
            }
            else if (entity is MyFloatingObject)
            {
                MyFloatingObject obj = entity as MyFloatingObject;
                materialType = (obj.VoxelMaterial != null) ? MyMaterialType.ROCK : MyMaterialType.METAL;
                surfaceImpact = MySurfaceImpactEnum.METAL;
            }
            else if (entity is MyTrees)
            {
                surfaceImpact = MySurfaceImpactEnum.DESTRUCTIBLE;
                materialType = MyMaterialType.WOOD;
            }
            else
            {
                surfaceImpact = MySurfaceImpactEnum.METAL;
                materialType = MyMaterialType.METAL;
                if (entity is MyCubeGrid)
                {
                    Vector3I blockPos;
                    var grid = (entity as MyCubeGrid);
                    if (grid != null)
                    {
                        grid.FixTargetCube(out blockPos, Vector3D.Transform(hitPosition, grid.PositionComp.WorldMatrixNormalizedInv) / grid.GridSize);
                        var block = grid.GetCubeBlock(blockPos);
                        if (block != null)
                        {
                            if (block.BlockDefinition.PhysicalMaterial != null)
                            {
                                materialType = MyStringHash.GetOrCompute(block.BlockDefinition.PhysicalMaterial.Id.SubtypeName);
                            }
                        }
                    }
                }
                if (materialType.GetHashCode() == 0) materialType = MyMaterialType.METAL;
            }
        }

        private void StopEffect()
        {
            //if (m_trailEffect != null)
            //{
            //    // stop the trail effect
            //    m_trailEffect.Stop();
            //    m_trailEffect = null;
            //}
        }

        private void CreateDecal(MyStringHash materialType)
        {
            //TODO Update decals for skinned objects
            //{
            //    //  Decal size depends on material. But for mining ship create smaller decal as original size looks to large on the ship.
            //    float decalSize = MyVRageUtils.GetRandomFloat(materialProperties.BulletHoleSizeMin,
            //                                                materialProperties.BulletHoleSizeMax) * m_ammoProperties.TrailScale;

            //    //  Place bullet hole decal
            //    var intersection = m_intersection.Value;
            //    float randomColor = MyVRageUtils.GetRandomFloat(0.5f, 1.0f);
            //    float decalAngle = MyVRageUtils.GetRandomRadian();

            //    MyDecals.Add(
            //        materialProperties.BulletHoleDecal,
            //        decalSize,
            //        decalAngle,
            //        new Vector4(randomColor, randomColor, randomColor, 1),
            //        false,
            //        ref intersection,
            //        0.0f,
            //        m_ammoProperties.DecalEmissivity * m_ammoProperties.TrailScale, MyDecalsConstants.DECAL_OFFSET_BY_NORMAL);
            //}
        }

        private void PlayHitSound(MyStringHash materialType, IMyEntity entity, Vector3D position)
        {
            if ((OwnerEntity == null) || !(OwnerEntity is MyWarhead)) // do not play bullet sound when coming from warheads
            {
                var emitter = MyAudioComponent.TryGetSoundEmitter();
                if (emitter == null)
                    return;

                ProfilerShort.Begin("Play projectile sound");
                emitter.SetPosition(m_position);
                emitter.SetVelocity(Vector3.Zero);
                MyAutomaticRifleGun rifleGun = m_weapon as MyAutomaticRifleGun;

                MySoundPair cueEnum = null;
                MyStringHash thisType;
                if (m_projectileAmmoDefinition.IsExplosive)
                    thisType = MyMaterialType.EXPBULLET;
                else if (rifleGun != null && rifleGun.GunBase.IsAmmoProjectile && m_projectileAmmoDefinition.ProjectileType == MyProjectileType.Bullet)
                    thisType = MyMaterialType.RIFLEBULLET;
                else if (m_projectileAmmoDefinition.ProjectileType == MyProjectileType.Bolt)
                    thisType = MyMaterialType.BOLT;
                else
                    thisType = MyMaterialType.GUNBULLET;

                cueEnum = MyMaterialPropertiesHelper.Static.GetCollisionCue(MyMaterialPropertiesHelper.CollisionType.Start,thisType, materialType);
                if (cueEnum.SoundId.IsNull && entity is MyVoxelBase)    // Play rock sounds if we have a voxel material that doesn't have an assigned sound for thisType
                {
                    materialType = MyMaterialType.ROCK;
                    cueEnum = MyMaterialPropertiesHelper.Static.GetCollisionCue(MyMaterialPropertiesHelper.CollisionType.Start, thisType, materialType);
                }

                if (MySession.Static != null && MySession.Static.Settings.RealisticSound)
                {
                    Func<bool> canHear = () => MySession.Static.ControlledEntity != null && MySession.Static.ControlledEntity.Entity == entity;
                    emitter.StoppedPlaying += (e) => { e.EmitterMethods[MyEntity3DSoundEmitter.MethodsEnum.CanHear].Remove(canHear); } ;
                    emitter.EmitterMethods[MyEntity3DSoundEmitter.MethodsEnum.CanHear].Add(canHear);
                }

                emitter.PlaySound(cueEnum, false);
                
                ProfilerShort.End();
            }
        }

        private void ApplyDeformationCubeGrid(Vector3D hitPosition, MyCubeGrid grid, float damage)
        {
            MatrixD gridInv = grid.PositionComp.WorldMatrixNormalizedInv;
            var hitPositionInObjectSpace = Vector3D.Transform(hitPosition, gridInv);
            var hitDirLoc = Vector3D.TransformNormal(m_directionNormalized, gridInv);

            float deformationOffset = 0.000664f * damage;
            float softAreaPlanar = 0.011904f * damage;
            float softAreaVertical = 0.008928f * damage;
            softAreaPlanar = MathHelper.Clamp(softAreaPlanar, grid.GridSize * 0.75f, grid.GridSize * 1.3f);
            softAreaVertical = MathHelper.Clamp(softAreaVertical, grid.GridSize * 0.9f, grid.GridSize * 1.3f);
            grid.Physics.ApplyDeformation(deformationOffset, softAreaPlanar, softAreaVertical, hitPositionInObjectSpace, hitDirLoc, MyDamageType.Bullet);
        }

        public static void ApplyProjectileForce(IMyEntity entity, Vector3D intersectionPosition, Vector3 normalizedDirection, bool isPlayerShip, float impulse)
        {
            //  If we hit model that belongs to physic object, apply some force to it (so it's thrown in the direction of shoting)
            if (entity.Physics != null && entity.Physics.Enabled)
            {
                entity.Physics.AddForce(
                        MyPhysicsForceType.APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE,
                        normalizedDirection * impulse,
                        intersectionPosition, Vector3.Zero);
            }
        }

        #endregion

        public void Close()
        {
            OwnerEntity = null;
            m_ignoreEntity = null;
            m_weapon = null;

            //  Don't stop sound
        }
    }
}