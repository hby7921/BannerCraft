package dev.mycraft.bridge;

import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.level.Level;
import net.minecraft.core.Registry;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.ResourceKey;
import net.minecraft.resources.Identifier;
import net.minecraft.world.entity.MobCategory;

/**
 * Invisible, hittable stand-in for a Bannerlord enemy (SkyCraft
 * SkyrimActorEntity pattern, MIT-licensed reference). Its own health never
 * drops: vanilla combat math (weapon, crit, enchantments, cooldown, invulnerability
 * frames) is applied and the resulting damage is captured for the bridge.
 */
public class BridgeTargetEntity extends LivingEntity {
	private static final ResourceKey<EntityType<?>> BRIDGE_TARGET_KEY =
			ResourceKey.create(BuiltInRegistries.ENTITY_TYPE.key(),
					Identifier.fromNamespaceAndPath(MycraftBridge.MOD_ID, "bridge_target"));

	public static final EntityType<BridgeTargetEntity> TYPE = Registry.register(
			BuiltInRegistries.ENTITY_TYPE,
			BRIDGE_TARGET_KEY,
			EntityType.Builder.<BridgeTargetEntity>of(BridgeTargetEntity::new, MobCategory.MISC)
					.sized(0.6F, 1.8F)
					.noSave()
					.noSummon()
					.noLootTable()
					.clientTrackingRange(10)
					.updateInterval(1)
					.build(BRIDGE_TARGET_KEY));

	private float capturedDamage;
	private boolean capturing;
	private int actorId;
	private boolean hostile = true;
	private net.minecraft.world.phys.Vec3 hostSpeed = net.minecraft.world.phys.Vec3.ZERO;

	public BridgeTargetEntity(EntityType<? extends BridgeTargetEntity> type, Level level) {
		super(type, level);
		this.setNoGravity(true);
		this.noPhysics = true;
		this.setInvisible(true);
		this.setSilent(true);
	}

	public static void register() {
		net.fabricmc.fabric.api.object.builder.v1.entity.FabricDefaultAttributeRegistry.register(
				TYPE, LivingEntity.createLivingAttributes());
	}

	public static BridgeTargetEntity spawn(MinecraftServer server, int actorId, double x, double y, double z) {
		ServerLevel level = server.overworld();
		BridgeTargetEntity entity = new BridgeTargetEntity(TYPE, level);
		entity.actorId = ActorRegistry.checkedId(actorId);
		entity.setPos(x, y, z);
		if (!level.addFreshEntity(entity)) return null;
		return entity;
	}

	public int actorId() { return actorId; }
	public void setHostile(boolean value) { hostile = value; }
	public boolean isHostile() { return hostile; }
	public void setHostSpeed(net.minecraft.world.phys.Vec3 speed) { hostSpeed = speed; }
	public void setHostHealth(double health,double maximum) {
		if(!Double.isFinite(health)||!Double.isFinite(maximum)||health<0||maximum<=0||health>maximum||maximum>10000)
			throw new IllegalArgumentException("invalid native actor health");
		double factor=MycraftBridge.CONFIG.mcToBlDamageFactor;
		double nativeMaximum=Math.clamp(maximum/factor,1,1024);
		getAttribute(net.minecraft.world.entity.ai.attributes.Attributes.MAX_HEALTH).setBaseValue(nativeMaximum);
		setHealth((float)Math.clamp(health/factor,0,nativeMaximum));
	}
	@Override public net.minecraft.world.phys.Vec3 getKnownSpeed() { return hostSpeed; }

	public void discardProxy() {
		WeaponRuntime.retireActor(actorId);
		MobRuntime.retireActor(actorId);
		this.discard();
	}

	public void reposition(double x, double y, double z) {
		this.teleportTo(x, y, z);
		this.fallDistance = 0.0F;
	}

	public void beginDamageCapture() {
		capturing = true;
		capturedDamage = 0.0F;
	}

	/** Returns and clears the damage captured during the last player.attack(). */
	public float takeCapturedDamage() {
		capturing = false;
		return capturedDamage;
	}

	@Override
	public boolean hurtServer(ServerLevel level, net.minecraft.world.damagesource.DamageSource source, float amount) {
		if (!TntRuntime.captures(source) && !MobRuntime.captures(actorId, source) && !WeaponRuntime.captures(actorId, source)
				&& (!capturing || !(source.getEntity() instanceof net.minecraft.world.entity.player.Player))) return false;
		return super.hurtServer(level, source, amount);
	}

	@Override
	protected void actuallyHurt(ServerLevel level, net.minecraft.world.damagesource.DamageSource source, float amount) {
		// Keep native hurtServer immunity frames, then capture the damage at the health-write hook.
		float damage = getDamageAfterMagicAbsorb(source, getDamageAfterArmorAbsorb(source, amount));
		if (TntRuntime.captures(source)) TntRuntime.captureDamage(actorId, source, damage);
		else if (MobRuntime.captures(actorId, source)) MobRuntime.captureDamage(actorId, source, damage);
		else if (!capturing) WeaponRuntime.captureDamage(actorId, source, damage);
		if (capturing && Float.isFinite(damage) && damage > 0) capturedDamage += damage;
	}

	@Override
	public boolean isInvulnerableTo(net.minecraft.server.level.ServerLevel level, net.minecraft.world.damagesource.DamageSource source) {
		// Damageable by players only; environment/void never harms the proxy.
		return !TntRuntime.captures(source) && !MobRuntime.captures(actorId, source) && !WeaponRuntime.captures(actorId, source)
				&& !(source.getEntity() instanceof net.minecraft.world.entity.player.Player);
	}

	@Override
	public boolean addEffect(net.minecraft.world.effect.MobEffectInstance effect, net.minecraft.world.entity.Entity source) {
		boolean accepted = super.addEffect(effect, source);
		// This hook receives the true source only after vanilla accepted an added/updated effect.
		// Periodic source-less onEffectUpdated callbacks must not overwrite that provenance.
		if (accepted) MobRuntime.effectApplied(this, effect, source);
		return accepted;
	}

	@Override
	protected void onEffectsRemoved(java.util.Collection<net.minecraft.world.effect.MobEffectInstance> effects) {
		super.onEffectsRemoved(effects);
		if (effects.stream().anyMatch(effect -> effect.is(net.minecraft.world.effect.MobEffects.WITHER))) MobRuntime.retireEffect(actorId);
	}

	@Override
	public void pushFromExplosion(net.minecraft.world.phys.Vec3 motion) {
		// Preserve vanilla exposure/resistance math, while Bannerlord remains position authority.
		TntRuntime.captureKnockback(actorId, motion);
	}

	@Override
	public void knockback(double power, double xd, double zd, net.minecraft.world.damagesource.DamageSource source, float damage, boolean comesFromEffect) {
		// Bannerlord owns the enemy's position; ignore Minecraft knockback.
	}

	@Override
	public boolean isPushable() {
		return false;
	}

	@Override
	public net.minecraft.world.entity.HumanoidArm getMainArm() {
		return net.minecraft.world.entity.HumanoidArm.RIGHT;
	}
}
