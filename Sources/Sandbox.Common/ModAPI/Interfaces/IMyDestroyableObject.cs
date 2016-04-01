using Sandbox.Common.ModAPI;
using Sandbox.Common.ObjectBuilders.Definitions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VRage.ModAPI;
using VRage.Utils;

namespace Sandbox.ModAPI.Interfaces
{
    public interface IMyDestroyableObject
    {
        void OnDestroy();
        bool DoDamage(float damage, MyStringHash damageType, bool sync, MyHitInfo? hitInfo = null, long attackerId = 0);// returns true if damage could be applied
        float Integrity { get; }

        /// <summary>
        /// The amount of point damage the surface of this object can reflect
        /// </summary>
        /// <remarks>
        /// We could derive this from integrity, but it would need to be adjusted for object size.
        /// Getting actual volume or surface area of a model doesn't currently exist, and even if approximated with 3I,
        /// the integrity values for the same cubeblock across small/large grids aren't constant.
        /// I.E. a large armor block has less than 125 x the integrity of a small one (similar to the volume problem that was fixed)
        /// Setting it directly for each object gives us more balance control anyway.
        /// </remarks>
        float ProjectileResistance { get; }

        /// <summary>
        /// When set to true, it should use MyDamageSystem damage routing.
        /// </summary>
        bool UseDamageSystem { get; }
    }
}
