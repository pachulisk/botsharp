module BotSharp.Infrastructure.EventBus.SqliteLogger

#nowarn "3261"

open System
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// SqliteLogger — default EventBus consumer
//
// Writes all BotEvents to the event_log table in SQLite.
// This is the "always-on" audit trail for all system activity.
// Query via /events command or direct SQL.
// ═══════════════════════════════════════════════════════════════════════════

/// Serialize event Data map to JSON string.
let private dataToJson (data: Map<string, string>) : string =
    if data.IsEmpty then "{}"
    else
        use ms = new MemoryStream()
        use w = new Utf8JsonWriter(ms)
        w.WriteStartObject()
        for kv in data do
            w.WriteString(kv.Key, kv.Value)
        w.WriteEndObject()
        w.Flush()
        Text.Encoding.UTF8.GetString(ms.ToArray())

/// Create a consumer function that writes events to SQLite.
let createConsumer (openDb: unit -> SqliteConnection) : BotEvent -> Async<unit> =
    fun evt -> async {
        try
            use conn = openDb ()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "INSERT INTO event_log (id, timestamp, category, kind, session_id, data, created_at) " +
                "VALUES (@id, @ts, @cat, @kind, @sid, @data, @now)"
            cmd.Parameters.AddWithValue("@id", evt.Id) |> ignore
            cmd.Parameters.AddWithValue("@ts", evt.Timestamp.ToUnixTimeMilliseconds()) |> ignore
            cmd.Parameters.AddWithValue("@cat", evt.Category) |> ignore
            cmd.Parameters.AddWithValue("@kind", evt.Kind) |> ignore
            cmd.Parameters.AddWithValue("@sid",
                match evt.SessionId with Some s -> box s | None -> box DBNull.Value) |> ignore
            cmd.Parameters.AddWithValue("@data", dataToJson evt.Data) |> ignore
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) |> ignore
            cmd.ExecuteNonQuery() |> ignore
        with ex ->
            eprintfn "[SqliteLogger] Failed to log event %s/%s: %s" evt.Category evt.Kind ex.Message
    }

/// Query recent events from event_log.
let queryEvents (conn: SqliteConnection) (categoryFilter: string option) (sessionFilter: string option) (limit: int) : BotEvent list =
    let mutable where = "WHERE 1=1"
    match categoryFilter with
    | Some c -> where <- where + sprintf " AND category = '%s'" c
    | None -> ()
    match sessionFilter with
    | Some s -> where <- where + sprintf " AND session_id = '%s'" s
    | None -> ()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sprintf "SELECT id, timestamp, category, kind, session_id, data FROM event_log %s ORDER BY timestamp DESC LIMIT %d" where limit
    use reader = cmd.ExecuteReader()
    let results = Collections.Generic.List<BotEvent>()
    while reader.Read() do
        let dataJson = if reader.IsDBNull(5) then "{}" else reader.GetString(5)
        let data =
            try
                use doc = JsonDocument.Parse(dataJson)
                [ for prop in doc.RootElement.EnumerateObject() ->
                    prop.Name, (if prop.Value.ValueKind = JsonValueKind.String then prop.Value.GetString() else prop.Value.GetRawText()) ]
                |> Map.ofList
            with _ -> Map.empty
        results.Add({
            Id        = reader.GetString(0)
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1))
            Category  = reader.GetString(2)
            Kind      = reader.GetString(3)
            SessionId = if reader.IsDBNull(4) then None else Some (reader.GetString(4))
            Data      = data
        })
    List.ofSeq results

/// Format events for /events command display.
let formatEvents (events: BotEvent list) : string =
    if events.IsEmpty then "(no events)"
    else
        let lines =
            events |> List.map (fun e ->
                let ts = e.Timestamp.ToString("HH:mm:ss")
                let sid = e.SessionId |> Option.map (fun s -> if s.Length > 12 then s.[..11] else s) |> Option.defaultValue ""
                let dataPreview =
                    e.Data |> Map.toList |> List.truncate 3
                    |> List.map (fun (k, v) -> sprintf "%s=%s" k (if v.Length > 30 then v.[..29] + ".." else v))
                    |> String.concat " "
                sprintf "  %s  %-24s %-14s %s" ts e.Kind sid dataPreview)
        sprintf "Recent Events (%d)\n%s\n%s"
            events.Length
            (String.replicate 70 "\u2500")
            (String.concat "\n" lines)
