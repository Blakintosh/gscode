using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace GSCode.Parser.DFA;

/// <summary>
/// Represents a game entity (Player, AI, etc.) in the script.
/// </summary>
internal class ScrEntity : ScrObject
{
    public required string EntityType { get; init; }
    public bool IsImmutable { get; init; } = false;
    private HashSet<string> _predefinedFields = new(StringComparer.OrdinalIgnoreCase);

    [SetsRequiredMembers]
    public ScrEntity() : base([], false)
    {
        EntityType = "entity";
    }

    [SetsRequiredMembers]
    protected ScrEntity(string entityType, IEnumerable<KeyValuePair<string, ScrData>> fields, bool immutable = false) : base(fields, false)
    {
        EntityType = entityType;
        IsImmutable = immutable;
        foreach (var field in fields)
        {
            _predefinedFields.Add(field.Key);
        }
    }

    public override SetResult Set(string fieldName, ScrData value)
    {
        if (IsImmutable)
        {
            return SetResult.Immutable;
        }

        if (_predefinedFields.Contains(fieldName))
        {
            var predefinedData = Fields[fieldName];
            if (predefinedData.ReadOnly)
            {
                return SetResult.ReadOnly;
            }

            // Check if types are compatible (must not change the type)
            if (!value.IsAny() && !predefinedData.IsAny() && (value.Type & predefinedData.Type) == 0)
            {
                return SetResult.TypeMismatch;
            }

            // For predefined fields, we don't carry out the assignment (keep original type record)
            return SetResult.Success;
        }

        // Custom field, treat as struct (allow adding/updating)
        return base.Set(fieldName, value);
    }

    public override ScrObject Copy()
    {
        ScrEntity newEntity = new(EntityType, [], IsImmutable);
        newEntity._predefinedFields = new HashSet<string>(_predefinedFields, StringComparer.OrdinalIgnoreCase);

        foreach (var field in Fields)
        {
            newEntity.Fields[field.Key] = field.Value.Copy();
        }

        return newEntity;
    }

    public static ScrEntity Weapon()
    {
        #region Weapon Fields
        return new(
            ScrEntityTypes.Weapon, [
            new("aifusetime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("projexplosionsound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("projexplosionsoundplayer", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("projSmokeStartSound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("projSmokeLoopSound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("projSmokeEndSound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("meleechargerange", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("meleelungerange", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("dogibbing", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("dogibbingonmelee", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("doannihilate", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("doblowback", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("maxgibdistance", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("leftarc", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("rightarc", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("toparc", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("bottomarc", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("clipmodel", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("fightdist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("maxdist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("spinuptime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("spindowntime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("fuellife", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isboltaction", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isdisallowatmatchstart", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("firesound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("firesoundplayer", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("blocksprone", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("iscliponly", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("lockOnRadius", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("lockOnLossRadius", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("requirelockontofire", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("setusedstat", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("maxinstancesallowed", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isemp", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isflash", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isstun", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("bulletImpactExplode", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("doempdestroyfx", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("dostun", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("dodamagefeedback", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("dohackedstats", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("hackertriggerorigintag", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("spawnInfluencer", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("anyplayercanretrieve", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("istacticalinsertion", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isvaluable", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("destroyablebytrophysystem", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("drawoffhandmodelinhand", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("disallowatmatchstart", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("nonstowedweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isscavengable", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("doesfiredamage", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("ignoresflakjacket", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("notkillstreak", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isgameplayweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("issupplydropweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("skipbattlechatterkill", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("skipbattlechatterreload", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("doNotDamageOwner", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("destroysEquipment", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("forcedamageshellshockandrumble", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isaikillstreakdamage", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("ignoreteamkills", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("teamkillpenaltyscale", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("vehicleprojectiledamagescalar", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("vehicleprojectilesplashdamagescalar", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("isballisticknife", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isperkbottle", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("skiplowammovox", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isflourishweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("ishybridweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("disableDeploy", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("issniperweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("ishacktoolweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("meleeIgnoresLightArmor", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("ignoresLightArmor", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("ignoresPowerArmor", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("soundRattleRangeMin", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("soundRattleRangeMax", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("grappleweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("burstCount", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("weaponHeadObjectiveHeight", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("enemycrosshairrange", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("unlimitedammo", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isnotdroppable", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("damageAlwaysKillsPlayer", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("damageToOwnerScalar", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("viewmodels", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("frontendmodel", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("worldmodel", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("worlddamagedmodel1", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("worlddamagedmodel2", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("worlddamagedmodel3", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("stowedmodel", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("shownenemyequip", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("shownenemyexplo", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("shownretrievable", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("lockonminrange", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("lockonscreenradius", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("lockonanglehorizontal", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("lockonanglevertical", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("lockonlossanglehorizontal", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("lockonlossanglevertical", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("isvalid", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("rootweapon", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("attachments", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("supportedattachments", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("startammo", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("maxammo", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("guidedmissiletype", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("lockontype", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isrocketlauncher", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("lockonSeekerSearchSound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("lockonSeekerSearchSoundLoops", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("lockonSeekerLockedSound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("lockonSeekerLockedSoundLoops", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("lockonTargetLockedSound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("lockonTargetLockedSoundLoops", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("lockonTargetFiredOnSound", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("lockonTargetFiredOnSoundLoops", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("type", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isbulletweapon", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isgrenadeweapon", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isprojectileweapon", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isgasweapon", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isriotshield", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("weapclass", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("iskillstreak", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("iscarriedkillstreak", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("offhandclass", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("offhandslot", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("islethalgrenade", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("istacticalgrenade", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isequipment", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isspecificuse", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("inventorytype", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isprimary", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isitem", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isaltmode", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("projexplosiontype", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isgadget", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isheroweapon", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_heroversion_2_0", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_breadcrumbduration", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_flickerondamage", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_flickeronpowerloss", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_flickeronpowerlow", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_max_hitpoints", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_power_consume_on_ammo_use", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_powermoveloss", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_powermovespeed", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("gadget_powergainscorefactor", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("gadget_powergainscoreignoreself", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_powergainscoreignorewhenactive", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_powerofflossondamage", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("gadget_poweronlossondamage", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("gadget_powerreplenishfactor", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("gadget_power_reset_on_spawn", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_power_reset_on_class_change", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_power_reset_on_team_change", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_power_reset_on_round_switch", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_power_round_end_active_penalty", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("gadget_power_usage_rate", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("gadget_powertakedowngain", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_takedownrevealtime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_type", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_shieldreflectpowergain", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_shieldreflectpowerloss", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_shockfield_radius", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("gadget_shockfield_damage", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
            new("gadget_turnoff_onempjammed", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("name", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("displayname", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("firetype", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("isfullauto", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("issemiauto", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isburstfire", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isstackedfire", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("isalllockedfire", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("ischargeshot", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("islauncher", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("clipsize", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("shotcount", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("ismeleeweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("deathcamtime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("firetime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("reloadtime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("meleetime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("meleepowertime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("meleepowertimeLeft", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("meleechargetime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("meleedamage", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("altweapon", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("dualwieldweapon", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("isdualwield", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("fusetime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("istimeddetonation", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("allowsDetonationDuringReload", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("proximitydetonation", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("proximityalarminnerradius", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("proximityalarmouterradius", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("proximityalarmactivationdelay", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("chaineventradius", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("chaineventtime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("chaineventmax", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("cookoffholdtime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("multidetonation", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("multidetonationfragmentspeed", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("explosionradius", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("explosioninnerradius", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("lockonmaxrange", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("lockonmaxrangenolineofsight", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("lockonspeed", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("ammocountequipment", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_powersprintloss", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_pulse_duration", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_pulse_margin", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_pulse_max_range", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("gadget_powermax", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("weaponstarthitpoints", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("weapondamage1hitpoints", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("weapondamage2hitpoints", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("weapondamage3hitpoints", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("nohitmarker", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("specialpain", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("decoy", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("altoffhand", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("dniweapon", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("pickupsound", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("pickupsoundplayer", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("burnDuration", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("burnDamageInterval", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("burnDamage", new ScrData(ScrDataTypes.Int, ReadOnly: true))
        ], true);
        #endregion
    }

    public static ScrEntity NonDeterministic()
    {
        return new("entity", [], false);
    }

    public static ScrEntity HudElem()
    {
        #region HudElem Fields
        return new(
            ScrEntityTypes.HudElem, [
            new("x", new ScrData(ScrDataTypes.Float)),
            new("y", new ScrData(ScrDataTypes.Float)),
            new("z", new ScrData(ScrDataTypes.Float)),
            new("fontscale", new ScrData(ScrDataTypes.Float)),
            new("font", new ScrData(ScrDataTypes.Int)),
            new("alignx", new ScrData(ScrDataTypes.Int)),
            new("aligny", new ScrData(ScrDataTypes.Int)),
            new("horzalign", new ScrData(ScrDataTypes.Int)),
            new("vertalign", new ScrData(ScrDataTypes.Int)),
            new("color", new ScrData(ScrDataTypes.Int)),
            new("alpha", new ScrData(ScrDataTypes.Int)),
            new("label", new ScrData(ScrDataTypes.Int)),
            new("sort", new ScrData(ScrDataTypes.Float)),
            new("foreground", new ScrData(ScrDataTypes.Bool)),
            new("hidewhendead", new ScrData(ScrDataTypes.Bool)),
            new("hidewheninkillcam", new ScrData(ScrDataTypes.Bool)),
            new("hidewhenindemo", new ScrData(ScrDataTypes.Bool)),
            new("immunetodemogamehudsettings", new ScrData(ScrDataTypes.Bool)),
            new("immunetodemofreecamera", new ScrData(ScrDataTypes.Bool)),
            new("hidewhileremotecontrolling", new ScrData(ScrDataTypes.Bool)),
            new("hidewheninmenu", new ScrData(ScrDataTypes.Bool)),
            new("hidewheninscope", new ScrData(ScrDataTypes.Bool)),
            new("fadewhentargeted", new ScrData(ScrDataTypes.Bool)),
            new("fontstyle3d", new ScrData(ScrDataTypes.Int)),
            new("font3duseglowcolor", new ScrData(ScrDataTypes.Bool)),
            new("showplayerteamhudelemtospectator", new ScrData(ScrDataTypes.Bool)),
            new("glowcolor", new ScrData(ScrDataTypes.Int)),
            new("glowalpha", new ScrData(ScrDataTypes.Int)),
            new("archived", new ScrData(ScrDataTypes.Bool))
        ]);
        #endregion
    }

    public static ScrEntity VehicleNode()
    {
        #region VehicleNode Fields
        return new(
            ScrEntityTypes.VehicleNode, [
            new("targetname", new ScrData(ScrDataTypes.String)),
            new("target", new ScrData(ScrDataTypes.String)),
            new("target2", new ScrData(ScrDataTypes.String)),
            new("script_linkname", new ScrData(ScrDataTypes.String)),
            new("script_noteworthy", new ScrData(ScrDataTypes.String)),
            new("origin", new ScrData(ScrDataTypes.Vector)),
            new("angles", new ScrData(ScrDataTypes.Vector)),
            new("speed", new ScrData(ScrDataTypes.Float)),
            new("radius", new ScrData(ScrDataTypes.Float)),
            new("lookahead", new ScrData(ScrDataTypes.Float)),
            new("tension", new ScrData(ScrDataTypes.Float)),
            new("spawnflags", new ScrData(ScrDataTypes.Int))
        ]);
        #endregion
    }

    public static ScrEntity PathNode()
    {
        #region PathNode Fields
        return new(
            ScrEntityTypes.PathNode, [
            new("targetname", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("target", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("animscript", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("script_linkname", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("script_noteworthy", new ScrData(ScrDataTypes.String, ReadOnly: true)),
            new("origin", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
            new("angles", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
            new("spawnflags", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("movementtype_ignore", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("movementtype_require", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("type", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
            new("suspended", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
            new("cost_modifier", new ScrData(ScrDataTypes.Float, ReadOnly: true))
        ], true);
        #endregion
    }

    public static ScrEntity AiType()
    {
        #region AiType Fields
        return new(
            ScrEntityTypes.AiType,
            GetGenericEntityFields().Concat(new[]
            {
                new KeyValuePair<string, ScrData>("hero", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("canflank", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("accuratefire", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("aggressivemode", new ScrData(ScrDataTypes.Bool, ReadOnly: true))
            }));
        #endregion
    }

    public static ScrEntity Vehicle()
    {
        #region Vehicle Fields
        return new(
            ScrEntityTypes.Vehicle,
            GetGenericEntityFields().Concat(new[]
            {
                new KeyValuePair<string, ScrData>("vehicleclass", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehicletype", new ScrData(ScrDataTypes.String)),
                new KeyValuePair<string, ScrData>("scriptvehicletype", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("playerdrivenversion", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehspeed", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("velocity", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("radius", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("height", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("turretrotscale", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("accuracy_turret", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("accuracy_gunner1", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("accuracy_gunner2", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("accuracy_gunner3", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("accuracy_gunner4", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("vehmodel", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehmodelenemy", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("deathmodel", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehviewmodel", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("modelswapdelay", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("radiusdamagemin", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("radiusdamagemax", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("radiusdamageradius", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("shootshock", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("rumbletype", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("rumblescale", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("rumbleduration", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("rumbleradius", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("rumblebasetime", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("rumbleadditionaltime", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("healthdefault", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("turretweapon", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("addtocompass", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("isphysicsvehicle", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("pathpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("pathlookpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("pathwidth", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("pathwidthlookaheadfrac", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("pathdistancetraveled", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("heliheightlockoffset", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("drivebysoundtime0", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("drivebysoundtime1", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("scriptbundlesettings", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("assassinationbundle", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehicleridersbundle", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehicleridersrobotbundle", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehkilloccupantsondeath", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("vehunlinkoccupantsondeath", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("vehcheckforpredictedcrash", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("vehonpath", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehaircraftcollisionenabled", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("turretontarget", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("turretonvistarget", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("gunner1ontarget", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("gunner2ontarget", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("physicslaunchdeathscale", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("jumpforce", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("predictedCollisionTime", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("glasscollision_alt", new ScrData(ScrDataTypes.Bool))
            }));
        #endregion
    }

    public static ScrEntity Sentient()
    {
        #region Sentient Fields
        return new(
            ScrEntityTypes.Sentient,
            GetGenericEntityFields().Concat(new[]
            {
                new KeyValuePair<string, ScrData>("script_owner", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("threatbias", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("threatbiasgroup", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("attacker", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("attackercount", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("node", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("prevnode", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("enemy", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("enemylastseenpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("scriptenemy", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("scriptenemytag", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("syncedmeleetarget", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("ignoreme", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("ignoreall", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("ignoreforfriendlyfire", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("ignorevortices", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("maxvisibledist", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("maxseenfovcosine", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("maxseenfovcosinez", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("silentshot", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("surprisedbymedistsq", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("attackeraccuracy", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("ignorenavmeshtriggers", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("ignorebulletdamage", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("inmeleecharge", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("updatesight", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("fovcosine", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("fovcosinebusy", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("fovcosinez", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("maxsightdistsqrd", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("sightlatency", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("ignoreclosefoliage", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("pacifist", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("pacifistwait", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("goodenemyonly", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("ignoreexplosionevents", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("favoriteenemy", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("highlyawareradius", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("drawoncompass", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("activatecrosshair", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("attackercountthreatscale", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("currentenemythreatscale", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("recentattackerthreatscale", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("coverthreatscale", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("goalradius", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("goalheight", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("goalpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("goalent", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("goalforced", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("fixednode", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("fixednodesaferadius", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("goalangle", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("isatanchor", new ScrData(ScrDataTypes.Bool))
            }));
        #endregion
    }

    public static ScrEntity Actor()
    {
        #region Actor Fields
        return new(
            ScrEntityTypes.Actor,
            GetGenericEntityFields().Concat(new[]
            {
                new KeyValuePair<string, ScrData>("isdog", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("missinglegs", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("accuracy", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("perfectaim", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("blindaim", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("holdfire", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("forcefire", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("damagetaken", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("damagedir", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("damageyaw", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("damagelocation", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("damageweapon", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("damagemod", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("proneok", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("walkdist", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("dontavoidplayer", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("desiredangle", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("newenemyreactionpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("newenemyreaction", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("newenemyreactiondistsq", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("ignoresuppression", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("suppressionwait", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("suppressionduration", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("suppressionstarttime", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("suppressionmeter", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("weapon", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("secondaryweapon", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("primaryweapon", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("sidearm", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("meleeweapon", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("ammopouch", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("grenadeawareness", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("grenade", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("grenadeweapon", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("grenadeammo", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("grenadethrowback", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("allowpain", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("blockingpain", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("diequietly", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("skipDeath", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("doingambush", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("combatmode", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("alertlevel", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("alertlevelint", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("awarenesslevelcurrent", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("awarenesslevelprevious", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("ignoretriggers", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("pushable", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("enableterrainik", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("noplayermeleeblood", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("clamptonavmesh", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("gibbed", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("groundrelativepose", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("dropweapon", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("groundtype", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("properName", new ScrData(ScrDataTypes.String)),
                new KeyValuePair<string, ScrData>("scriptstate", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("lastscriptstate", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("statechangereason", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("nodeoffsetpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("pathstartpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("pathgoalpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("pathrandompercent", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("usechokepoints", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("lastenemysightpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("pathenemylookahead", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("pathenemyfightdist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("ignorepathenemyfightdist", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("ignorerunandgundist", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("runandgundist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("meleeattackdist", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("movemode", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("usecombatscriptatcover", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("safetochangescript", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("usegoalanimweight", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("keepclaimednode", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("keepclaimednodeifvalid", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("coversearchinterval", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("nextFindBestCoverTime", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("badplaceawareness", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("nogrenadereturnthrow", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("goodshootpos", new ScrData(ScrDataTypes.Vector)),
                new KeyValuePair<string, ScrData>("goodshootposvalid", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("flashbangimmunity", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("lookaheaddir", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("lookaheaddist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("prevanimdelta", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("exposedduration", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("pathwaittime", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("lastpathtime", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("isarriving", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("isarrivalpending", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("engagemindist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("engageminfalloffdist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("engagemaxdist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("engagemaxfalloffdist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("finalaccuracy", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("noattackeraccuracymod", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("maxfaceenemydist", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("prevrelativedir", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("relativedir", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("gunblockedbywall", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("fixedlinkyawonly", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("weaponaccuracy", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("movementtype", new ScrData(ScrDataTypes.String)),
                new KeyValuePair<string, ScrData>("arrivalfinalpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("exitPos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("predictedExitPos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("predictedArrivalDirectionValid", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("arrivalfinalyaw", new ScrData(ScrDataTypes.Float, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("firemode", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("traversestartnode", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("traverseendnode", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("traversalstartpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("traversalendpos", new ScrData(ScrDataTypes.Vector, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("manualtraversemode", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("script_accuracy", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("bulletsinclip", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("actor_id", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("animtranslationscale", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("pathablematerial", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("hero", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("canflank", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("accuratefire", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("aggressivemode", new ScrData(ScrDataTypes.Bool, ReadOnly: true))
            }));
        #endregion
    }

    public static ScrEntity Player()
    {
        #region Player Fields
        return new(
            ScrEntityTypes.Player,
            GetGenericEntityFields().Concat(new[]
            {
                new KeyValuePair<string, ScrData>("playername", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("sessionteam", new ScrData(ScrDataTypes.String)),
                new KeyValuePair<string, ScrData>("name", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("maxhealth", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("weaponhealth", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("hasspyplane", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("hassatellite", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("disallowvehicleusage", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("downs", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("revives", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("kills", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("deaths", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("assists", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("defends", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("plants", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("defuses", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("returns", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("captures", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("objtime", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("destructions", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("disables", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("escorts", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("carries", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("throws", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("survived", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("stabs", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("tomahawks", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("humiliated", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("x2score", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("headshots", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("agrkills", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("hacks", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("pointstowin", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("killsconfirmed", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("killsdenied", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("shotsmissed", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("shotshit", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("victory", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("sbtimeplayed", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("incaps", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("gems", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("skulls", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("chickens", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("killcamentity", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("killcamtargetentity", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("killcamweapon", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("killcammod", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("spectatekillcam", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("score", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("sessionstate", new ScrData(ScrDataTypes.String)),
                new KeyValuePair<string, ScrData>("statusicon", new ScrData(ScrDataTypes.String)),
                new KeyValuePair<string, ScrData>("spectatorclient", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("currentspectatingclient", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("archivetime", new ScrData(ScrDataTypes.Float)),
                new KeyValuePair<string, ScrData>("psoffsettime", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("pers", new ScrData(ScrDataTypes.Object, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("usingvehicle", new ScrData(ScrDataTypes.Bool, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("vehicleposition", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("headicon", new ScrData(ScrDataTypes.String)),
                new KeyValuePair<string, ScrData>("momentum", new ScrData(ScrDataTypes.Int)),
                new KeyValuePair<string, ScrData>("divetoprone", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("sprinting", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("animViewUnlock", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("animInputUnlock", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("animNoClientTransform", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("topdowncamera", new ScrData(ScrDataTypes.Bool)),
                new KeyValuePair<string, ScrData>("groundentity", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("viewlockedentity", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("cursorhintent", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("groundsurfacetype", new ScrData(ScrDataTypes.String, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("lookatent", new ScrData(ScrDataTypes.Entity, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("chargeshotlevel", new ScrData(ScrDataTypes.Int, ReadOnly: true)),
                new KeyValuePair<string, ScrData>("lockonentity", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("pivotentity", new ScrData(ScrDataTypes.Entity)),
                new KeyValuePair<string, ScrData>("lastdamagetime", new ScrData(ScrDataTypes.Int, ReadOnly: true))
            }));
        #endregion
    }

    /// <summary>
    /// Returns an enumerable of generic entity fields that can be used for any entity type.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, ScrData>> GetGenericEntityFields()
    {
        return new[]
        {
            new KeyValuePair<string, ScrData>("aitype", new ScrData(ScrDataTypes.Object)),
            new KeyValuePair<string, ScrData>("aitypevariant", new ScrData(ScrDataTypes.Object)),
            new KeyValuePair<string, ScrData>("angles", new ScrData(ScrDataTypes.Vector)),
            new KeyValuePair<string, ScrData>("animname", new ScrData(ScrDataTypes.String)),
            new KeyValuePair<string, ScrData>("archetype", new ScrData(ScrDataTypes.Object)),
            new KeyValuePair<string, ScrData>("species", new ScrData(ScrDataTypes.Object)),
            new KeyValuePair<string, ScrData>("scoretype", new ScrData(ScrDataTypes.Object)),
            new KeyValuePair<string, ScrData>("birthtime", new ScrData(ScrDataTypes.Int)),
            new KeyValuePair<string, ScrData>("classname", new ScrData(ScrDataTypes.String)),
            new KeyValuePair<string, ScrData>("count", new ScrData(ScrDataTypes.Int)),
            new KeyValuePair<string, ScrData>("dmg", new ScrData(ScrDataTypes.Int)),
            new KeyValuePair<string, ScrData>("health", new ScrData(ScrDataTypes.Int)),
            new KeyValuePair<string, ScrData>("index", new ScrData(ScrDataTypes.Int)),
            new KeyValuePair<string, ScrData>("item", new ScrData(ScrDataTypes.Entity)),
            new KeyValuePair<string, ScrData>("lerp_to_lighter", new ScrData(ScrDataTypes.Float)),
            new KeyValuePair<string, ScrData>("lerp_to_darker", new ScrData(ScrDataTypes.Float)),
            new KeyValuePair<string, ScrData>("model", new ScrData(ScrDataTypes.String)),
            new KeyValuePair<string, ScrData>("origin", new ScrData(ScrDataTypes.Vector)),
            new KeyValuePair<string, ScrData>("script_animname", new ScrData(ScrDataTypes.String)),
            new KeyValuePair<string, ScrData>("script_noteworthy", new ScrData(ScrDataTypes.String)),
            new KeyValuePair<string, ScrData>("spawnflags", new ScrData(ScrDataTypes.Int)),
            new KeyValuePair<string, ScrData>("takedamage", new ScrData(ScrDataTypes.Bool)),
            new KeyValuePair<string, ScrData>("allowdeath", new ScrData(ScrDataTypes.Bool)),
            new KeyValuePair<string, ScrData>("target", new ScrData(ScrDataTypes.String)),
            new KeyValuePair<string, ScrData>("targetname", new ScrData(ScrDataTypes.String)),
            new KeyValuePair<string, ScrData>("team", new ScrData(ScrDataTypes.Int))
        };
    }
}

internal static class ScrEntityTypes
{
    public const string Weapon = "weapon";
    public const string Vehicle = "vehicle";
    public const string Player = "player";
    public const string Actor = "actor";
    public const string AiType = "aitype";
    public const string PathNode = "pathnode";
    public const string Sentient = "sentient";
    public const string VehicleNode = "vehiclenode";
    public const string HudElem = "hudelem";
}