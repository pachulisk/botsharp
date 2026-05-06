// Formal verification of the streaming event accumulator.
// Corresponds to the emitter logic in Application/AgentLoop.fs (streaming branch).
//
// Proves:
//   1. TextAccIsConcat         — textAcc equals the concatenation of all TextDelta contents
//   2. NoToolCallsEmptyBuffers — no tool-call events → toolBuffers stays empty
//   3. EmptyResponseAlignment  — thinking-only events → textAcc="" and toolBuffers=∅

module StreamAccumulator {

  datatype StreamDelta =
    | TextDelta(content: string)
    | ThinkingDelta(content: string)
    | ToolArgDelta(index: int, chunk: string)

  // StreamEvent mirrors Domain/Types.fs StreamEvent (with index added to ToolCallStarted).
  datatype StreamEvent =
    | ContentDelta(delta: StreamDelta)
    | ToolCallStarted(index: int, id: string, name: string)
    | ToolCallCompleted               // reserved for Anthropic API; not emitted by current provider
    | StreamCompleted                 // reserved for Anthropic API; not emitted by current provider
    | StreamError(message: string)    // non-fatal; adapter continues reading

  datatype ToolCallBuffer = ToolCallBuffer(id: string, name: string, args: string)

  datatype AccumState = AccumState(
    textAcc: string,
    thinkingAcc: string,
    toolBuffers: map<int, ToolCallBuffer>
  )

  function EmptyState(): AccumState {
    AccumState("", "", map[])
  }

  function ProcessEvent(s: AccumState, evt: StreamEvent): AccumState {
    match evt {
      case ContentDelta(TextDelta(t))          => s.(textAcc := s.textAcc + t)
      case ContentDelta(ThinkingDelta(t))      => s.(thinkingAcc := s.thinkingAcc + t)
      case ContentDelta(ToolArgDelta(idx, chunk)) =>
        if idx in s.toolBuffers then
          var b := s.toolBuffers[idx];
          s.(toolBuffers := s.toolBuffers[idx := ToolCallBuffer(b.id, b.name, b.args + chunk)])
        else s   // orphaned arg chunk before ToolCallStarted — ignored
      case ToolCallStarted(idx, id, name) =>
        if idx in s.toolBuffers then
          var b := s.toolBuffers[idx];
          s.(toolBuffers := s.toolBuffers[idx := ToolCallBuffer(id, name, b.args)])
        else
          s.(toolBuffers := s.toolBuffers[idx := ToolCallBuffer(id, name, "")])
      case _ => s   // StreamError / ToolCallCompleted / StreamCompleted — no state change
    }
  }

  function ProcessEventsAcc(s: AccumState, events: seq<StreamEvent>): AccumState
    decreases |events|
  {
    if |events| == 0 then s
    else ProcessEventsAcc(ProcessEvent(s, events[0]), events[1..])
  }

  function ProcessEvents(events: seq<StreamEvent>): AccumState {
    ProcessEventsAcc(EmptyState(), events)
  }

  // ── Helper: concatenate all TextDelta contents in an event sequence ──────

  function ConcatText(events: seq<StreamEvent>): string
    decreases |events|
  {
    if |events| == 0 then ""
    else match events[0] {
      case ContentDelta(TextDelta(t)) => t + ConcatText(events[1..])
      case _                          => ConcatText(events[1..])
    }
  }

  // ── Lemma 1: textAcc equals the concatenation of all TextDelta contents ──

  lemma TextAccIsConcat(events: seq<StreamEvent>)
    ensures ProcessEvents(events).textAcc == ConcatText(events)
  {
    TextAccIsConcatAcc(EmptyState(), events);
  }

  lemma TextAccIsConcatAcc(s: AccumState, events: seq<StreamEvent>)
    ensures ProcessEventsAcc(s, events).textAcc == s.textAcc + ConcatText(events)
    decreases |events|
  {
    if |events| == 0 {
    } else {
      var s' := ProcessEvent(s, events[0]);
      TextAccIsConcatAcc(s', events[1..]);
    }
  }

  // ── Lemma 2: no tool-call events → toolBuffers stays empty ──────────────

  lemma NoToolCallsEmptyBuffers(events: seq<StreamEvent>)
    requires forall e :: e in events ==>
             (!e.ContentDelta? || !e.delta.ToolArgDelta?) && !e.ToolCallStarted?
    ensures ProcessEvents(events).toolBuffers == map[]
  {
    NoToolCallsEmptyBuffersAcc(EmptyState(), events);
  }

  lemma NoToolCallsEmptyBuffersAcc(s: AccumState, events: seq<StreamEvent>)
    requires s.toolBuffers == map[]
    requires forall e :: e in events ==>
             (!e.ContentDelta? || !e.delta.ToolArgDelta?) && !e.ToolCallStarted?
    ensures ProcessEventsAcc(s, events).toolBuffers == map[]
    decreases |events|
  {
    if |events| == 0 {
    } else {
      var s' := ProcessEvent(s, events[0]);
      assert s'.toolBuffers == map[];
      NoToolCallsEmptyBuffersAcc(s', events[1..]);
    }
  }

  // ── Lemma 3: thinking-only events → textAcc="" and toolBuffers=∅ ─────────

  lemma EmptyResponseAlignment(events: seq<StreamEvent>)
    requires forall e :: e in events ==>
             (e.ContentDelta? ==> e.delta.ThinkingDelta?) && !e.ToolCallStarted?
    ensures ProcessEvents(events).textAcc == ""
    ensures ProcessEvents(events).toolBuffers == map[]
  {
    EmptyResponseAlignmentAcc(EmptyState(), events);
  }

  lemma EmptyResponseAlignmentAcc(s: AccumState, events: seq<StreamEvent>)
    requires s.textAcc == "" && s.toolBuffers == map[]
    requires forall e :: e in events ==>
             (e.ContentDelta? ==> e.delta.ThinkingDelta?) && !e.ToolCallStarted?
    ensures ProcessEventsAcc(s, events).textAcc == ""
    ensures ProcessEventsAcc(s, events).toolBuffers == map[]
    decreases |events|
  {
    if |events| == 0 {
    } else {
      var s' := ProcessEvent(s, events[0]);
      EmptyResponseAlignmentAcc(s', events[1..]);
    }
  }
}
