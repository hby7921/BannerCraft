using System;
using System.Collections.Generic;

namespace Mycraft.Bridge;

/// <summary>
/// Consumer-side session/event gate (protocol §2/§4): pv check, session match,
/// bounded event dedup. Game thread owns one instance per battle session.
/// </summary>
public sealed class BridgeSessionValidator
{
    public enum Verdict
    {
        Ok,
        BadPv,
        NotInSession,
        StaleSession,
        UnknownType,
        Malformed,
        StaleSequence
    }

    private readonly DedupSet _events = new DedupSet(DemoProtocol.DedupCapacity);
    private readonly HashSet<string> _sessionTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "begin_session", "end_session", "player_pose", "actor_upsert", "actor_remove",
        "attack_input", "hit_actor", "player_hurt", "player_state", "ack", "select_slot",
        "inventory_request","inventory_state","inventory_swap","creative_give","build_request","block_state","build_result","inventory_result","use_item",
        "interact","mine","interaction_result","player_hurt_result","staging_origin","tnt_state","explosion","explosion_hit","weapon_hit","projectile_state","player_motion","stop_spin","lightning_state",
        "mob_state","mob_hit","mob_hurt","mob_remove","mob_ground","mob_summon","mc_army_spawn","mc_army_spawn_result","boat_state","boat_remove"
    };

    public string SessionId { get; private set; } = "";

    public bool SessionActive => SessionId.Length > 0;
    public bool EventsFull => _events.IsFull;
    public bool HasEvent(string eventId) => _events.Contains(eventId);
    private long _lastSeq;

    public void ResetConnection() { EndSession(); _lastSeq = 0; }

    public void BeginSession(string sessionId)
    {
        SessionId = sessionId ?? "";
        _events.Clear();
    }

    public void EndSession()
    {
        SessionId = "";
        _events.Clear();
    }

    public Verdict Validate(Dictionary<string, object> msg)
    {
        if (msg == null)
            return Verdict.UnknownType;
        var type = DemoProtocol.S(msg, "type");
        if (type.Length == 0)
            return Verdict.UnknownType;
        if (!DemoProtocol.ValidatePv(msg)) return Verdict.BadPv;
        if (!DemoProtocol.ValidateMessage(msg)) return Verdict.Malformed;
        var seq = DemoProtocol.L(msg, "seq");
        if (seq <= _lastSeq) return Verdict.StaleSequence;
        _lastSeq = seq;
        if (type == "hello" || type == "hello_ack" || type == "heartbeat" || type == "error")
            return DemoProtocol.ValidatePv(msg) ? Verdict.Ok : Verdict.BadPv;
        if (!_sessionTypes.Contains(type))
            return Verdict.UnknownType;
        if (!DemoProtocol.ValidatePv(msg))
            return Verdict.BadPv;
        if (!SessionActive)
            return Verdict.NotInSession;
        var sid = DemoProtocol.S(msg, "sid");
        return string.Equals(sid, SessionId, StringComparison.Ordinal) ? Verdict.Ok : Verdict.StaleSession;
    }

    /// <summary>True on first sight of eventId; false on duplicate (already applied).</summary>
    public bool TryMarkEvent(string eventId) => _events.TryMark(eventId);
}
