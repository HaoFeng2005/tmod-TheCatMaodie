using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace TheCatMaodie.Projectiles
{
	public class DecayBeam : ModProjectile
	{
		private Vector2 spawnPos;

		public override void SetDefaults()
		{
			Projectile.width = 30;
			Projectile.height = 30;
			Projectile.DamageType = DamageClass.Melee;
			Projectile.friendly = true;
			Projectile.hostile = false;
			Projectile.penetrate = 3;
			Projectile.timeLeft = 60;
		}

		public override string Texture => "Terraria/Images/Projectile_502";


		public override void AI()
		{
			if (Projectile.ai[0] == 0)
			{
				spawnPos = Projectile.position;
				Projectile.ai[0] = 1;
			}

			float distance = Vector2.Distance(Projectile.position, spawnPos);
			float maxRange = 300f;
			float falloff = MathHelper.Clamp(distance / maxRange, 0f, 1f);
			float damageScale = 1f - falloff;

			Projectile.damage = (int)(Projectile.originalDamage * damageScale);
			 Projectile.rotation = Projectile.velocity.ToRotation();
		}
	}
}
