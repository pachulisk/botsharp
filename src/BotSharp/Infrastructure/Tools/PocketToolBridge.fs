module BotSharp.Infrastructure.Tools.PocketToolBridge

open System
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Channels.PocketChannel

// ═══════════════════════════════════════════════════════════════════════════
// PocketToolBridge — registers HostBridge JSON-RPC methods as BotSharp tools
//
// Instead of routing through MCP, calls HostBridge methods directly via
// PocketRpcClient. The HostBridgeJsonRpcRouter on the Android side handles
// dispatch to the appropriate RPC adapter.
//
// Tools are defined as data (ToolDef records) and converted to
// ToolSpec + executor pairs programmatically.
// ═══════════════════════════════════════════════════════════════════════════

// ── Tool definition DSL ──────────────────────────────────────────────────────

type private ParamDef = {
    Name        : string
    Type        : JsonSchemaType
    Description : string
    Required    : bool
}

type private ToolDef = {
    Method      : string          // JSON-RPC method name (e.g., "clipboard.get")
    ToolName    : string          // BotSharp tool name (e.g., "pocket_clipboard_get")
    Description : string
    Params      : ParamDef list
}

let private req name ty desc = { Name = name; Type = ty; Description = desc; Required = true }
let private opt name ty desc = { Name = name; Type = ty; Description = desc; Required = false }

// ── Tool definitions ─────────────────────────────────────────────────────────

let private toolDefs : ToolDef list = [
    // ── Clipboard ────────────────────────────────────────────────────────────
    { Method = "clipboard.get"; ToolName = "pocket_clipboard_get"
      Description = "Read the current clipboard content from the device"
      Params = [] }

    { Method = "clipboard.set"; ToolName = "pocket_clipboard_set"
      Description = "Copy text to the device clipboard"
      Params = [req "text" JsString "Text to copy to clipboard"
                opt "label" JsString "Optional label for the clipboard entry"] }

    // ── Device info ──────────────────────────────────────────────────────────
    { Method = "device.info"; ToolName = "pocket_device_info"
      Description = "Get device information (model, manufacturer, OS version, etc.)"
      Params = [] }

    { Method = "device.battery"; ToolName = "pocket_device_battery"
      Description = "Get battery level and charging status"
      Params = [] }

    { Method = "device.display"; ToolName = "pocket_device_display"
      Description = "Get display metrics (resolution, density, etc.)"
      Params = [] }

    { Method = "device.network_info"; ToolName = "pocket_device_network"
      Description = "Get network connection info (WiFi, cellular, etc.)"
      Params = [] }

    // ── Contacts ─────────────────────────────────────────────────────────────
    { Method = "contact.search"; ToolName = "pocket_contact_search"
      Description = "Search contacts by name or phone number"
      Params = [req "query" JsString "Search query (name or phone number)"
                opt "limit" JsNumber "Maximum number of results (default: 20)"] }

    { Method = "contact.get"; ToolName = "pocket_contact_get"
      Description = "Get detailed info for a specific contact"
      Params = [req "contact_id" JsString "Contact ID from search results"] }

    { Method = "contact.add"; ToolName = "pocket_contact_add"
      Description = "Create a new contact"
      Params = [req "name" JsString "Contact display name"
                opt "phone" JsString "Phone number"
                opt "phone_type" JsString "Phone type: mobile, home, work"
                opt "email" JsString "Email address"
                opt "email_type" JsString "Email type: home, work"
                opt "company" JsString "Company name"
                opt "title" JsString "Job title"
                opt "note" JsString "Note"] }

    // ── Calendar ─────────────────────────────────────────────────────────────
    { Method = "calendar.list_events"; ToolName = "pocket_calendar_list"
      Description = "List calendar events within a date range"
      Params = [opt "from" JsString "Start date (ISO 8601, default: today)"
                opt "to" JsString "End date (ISO 8601, default: +7 days)"
                opt "limit" JsNumber "Maximum events to return"
                opt "query" JsString "Search query to filter events"] }

    { Method = "calendar.add_event"; ToolName = "pocket_calendar_add"
      Description = "Create a new calendar event"
      Params = [req "title" JsString "Event title"
                req "start" JsString "Start time (ISO 8601)"
                opt "end" JsString "End time (ISO 8601)"
                opt "all_day" JsBoolean "True for all-day event"
                opt "location" JsString "Event location"
                opt "description" JsString "Event description"] }

    { Method = "calendar.delete_event"; ToolName = "pocket_calendar_delete"
      Description = "Delete a calendar event"
      Params = [req "event_id" JsString "Event ID to delete"] }

    // ── Phone calls ──────────────────────────────────────────────────────────
    { Method = "call.list_calls"; ToolName = "pocket_call_list"
      Description = "List recent call history"
      Params = [opt "type" JsString "Filter: incoming, outgoing, missed"
                opt "limit" JsNumber "Maximum entries to return"] }

    { Method = "call.dial"; ToolName = "pocket_call_dial"
      Description = "Dial a phone number (requires user confirmation)"
      Params = [req "number" JsString "Phone number to dial"] }

    // ── SMS (direct JSON-RPC, not MCP) ───────────────────────────────────────
    { Method = "sms.list_threads"; ToolName = "pocket_sms_list"
      Description = "List SMS conversation threads sorted by recent activity"
      Params = [opt "query" JsString "Search query to filter threads"
                opt "limit" JsNumber "Maximum threads to return"] }

    { Method = "sms.read_thread"; ToolName = "pocket_sms_read"
      Description = "Read messages from an SMS thread"
      Params = [req "thread_id" JsString "Thread ID from list results"
                opt "limit" JsNumber "Maximum messages to return"] }

    { Method = "sms.send"; ToolName = "pocket_sms_send"
      Description = "Send an SMS message (requires user confirmation)"
      Params = [req "address" JsString "Recipient phone number"
                req "body" JsString "Message text"] }

    // ── File operations ──────────────────────────────────────────────────────
    { Method = "file.pick"; ToolName = "pocket_file_pick"
      Description = "Open file picker dialog for user to select a file"
      Params = [opt "type" JsString "MIME type filter (e.g., image/*, application/pdf)"
                opt "dest" JsString "Destination directory for copied file"] }

    { Method = "file.share"; ToolName = "pocket_file_share"
      Description = "Share a file via Android share sheet"
      Params = [req "path" JsString "File path to share"
                opt "type" JsString "MIME type"
                opt "title" JsString "Share dialog title"] }

    // ── Notifications ────────────────────────────────────────────────────────
    { Method = "notification.send"; ToolName = "pocket_notification_send"
      Description = "Send a local notification on the device"
      Params = [req "title" JsString "Notification title"
                req "message" JsString "Notification body text"
                opt "tag" JsString "Tag for grouping/replacing notifications"
                opt "priority" JsString "Priority: low, default, high"] }

    { Method = "notification.list_active"; ToolName = "pocket_notification_list"
      Description = "List currently active notifications"
      Params = [] }

    // ── UI dialogs ───────────────────────────────────────────────────────────
    { Method = "ui.confirm"; ToolName = "pocket_ui_confirm"
      Description = "Show a confirmation dialog and wait for user response"
      Params = [req "title" JsString "Dialog title"
                opt "message" JsString "Dialog message"
                opt "positive" JsString "Positive button text (default: OK)"
                opt "negative" JsString "Negative button text (default: Cancel)"] }

    { Method = "ui.alert"; ToolName = "pocket_ui_alert"
      Description = "Show an alert dialog"
      Params = [req "title" JsString "Dialog title"
                opt "message" JsString "Alert message"
                opt "button" JsString "Button text (default: OK)"] }

    { Method = "ui.input"; ToolName = "pocket_ui_input"
      Description = "Show an input dialog and get text from user"
      Params = [req "title" JsString "Dialog title"
                opt "message" JsString "Input prompt"
                opt "hint" JsString "Placeholder text"
                opt "default" JsString "Default value"
                opt "input_type" JsString "Input type: text, number, email, phone, password"
                opt "max_length" JsNumber "Maximum input length"] }

    { Method = "ui.toast"; ToolName = "pocket_ui_toast"
      Description = "Show a brief toast message"
      Params = [req "message" JsString "Toast message text"
                opt "duration" JsString "Duration: short, long"] }

    // ── Chrome / Web ─────────────────────────────────────────────────────────
    { Method = "chrome.search"; ToolName = "pocket_chrome_search"
      Description = "Search the web using the device browser"
      Params = [req "query" JsString "Search query"
                opt "engine" JsString "Search engine: google, bing, duckduckgo"
                opt "limit" JsNumber "Number of results to return"] }

    { Method = "chrome.fetch"; ToolName = "pocket_chrome_fetch"
      Description = "Fetch and extract content from a web page"
      Params = [req "url" JsString "URL to fetch"
                opt "selector" JsString "CSS selector to extract specific content"
                opt "format" JsString "Output format: text, html, markdown"] }

    // ── Camera & Gallery ─────────────────────────────────────────────────────
    { Method = "camera.capture_photo"; ToolName = "pocket_camera_photo"
      Description = "Take a photo using the device camera"
      Params = [opt "output" JsString "Output file path"] }

    { Method = "gallery.pick"; ToolName = "pocket_gallery_pick"
      Description = "Open gallery picker to select an image"
      Params = [] }

    { Method = "gallery.list_photos"; ToolName = "pocket_gallery_list"
      Description = "List photos on the device"
      Params = [opt "album_id" JsString "Album ID to filter"
                opt "limit" JsNumber "Maximum photos to return"] }

    // ── Location ─────────────────────────────────────────────────────────────
    { Method = "location.current"; ToolName = "pocket_location_current"
      Description = "Get the device's current GPS location"
      Params = [] }

    { Method = "location.geocode"; ToolName = "pocket_location_geocode"
      Description = "Convert an address to coordinates"
      Params = [req "address" JsString "Address to geocode"] }

    // ── App management ───────────────────────────────────────────────────────
    { Method = "app.list_installed"; ToolName = "pocket_app_list"
      Description = "List installed applications on the device"
      Params = [opt "limit" JsNumber "Maximum apps to return"] }

    { Method = "app.launch"; ToolName = "pocket_app_launch"
      Description = "Launch an installed application"
      Params = [req "package_name" JsString "App package name (e.g., com.whatsapp)"] }

    { Method = "app.open_url"; ToolName = "pocket_app_open_url"
      Description = "Open a URL in the default browser or handler"
      Params = [req "url" JsString "URL to open"] }

    // ── Screen ───────────────────────────────────────────────────────────────
    { Method = "screen.capture"; ToolName = "pocket_screen_capture"
      Description = "Take a screenshot of the current screen"
      Params = [opt "output" JsString "Output file path"] }

    // ── Audio ────────────────────────────────────────────────────────────────
    { Method = "audio.record"; ToolName = "pocket_audio_record"
      Description = "Record audio from the microphone"
      Params = [opt "output" JsString "Output file path"
                opt "max_seconds" JsNumber "Maximum recording duration in seconds"] }

    { Method = "audio.play"; ToolName = "pocket_audio_play"
      Description = "Play an audio file"
      Params = [req "path" JsString "Audio file path to play"] }

    // ── TTS ──────────────────────────────────────────────────────────────────
    { Method = "tts.speak"; ToolName = "pocket_tts_speak"
      Description = "Convert text to speech and play it"
      Params = [req "text" JsString "Text to speak"
                opt "language" JsString "Language code (e.g., en-US, zh-CN)"] }

    // ── Settings ─────────────────────────────────────────────────────────────
    { Method = "settings.get_brightness"; ToolName = "pocket_settings_brightness_get"
      Description = "Get current screen brightness level"
      Params = [] }

    { Method = "settings.set_brightness"; ToolName = "pocket_settings_brightness_set"
      Description = "Set screen brightness level"
      Params = [req "level" JsNumber "Brightness level (0-255)"] }

    { Method = "settings.get_volume"; ToolName = "pocket_settings_volume_get"
      Description = "Get current volume levels"
      Params = [] }

    { Method = "settings.set_volume"; ToolName = "pocket_settings_volume_set"
      Description = "Set volume level for a specific stream"
      Params = [req "stream" JsString "Audio stream: music, ring, alarm, notification"
                req "level" JsNumber "Volume level"] }

    // ── Alarm ────────────────────────────────────────────────────────────────
    { Method = "alarm.set_alarm"; ToolName = "pocket_alarm_set"
      Description = "Set an alarm"
      Params = [req "hour" JsNumber "Hour (0-23)"
                req "minute" JsNumber "Minute (0-59)"
                opt "label" JsString "Alarm label"
                opt "days" (JsArray JsNumber) "Days to repeat (1=Mon, 7=Sun)"] }

    { Method = "alarm.set_timer"; ToolName = "pocket_alarm_timer"
      Description = "Set a countdown timer"
      Params = [req "duration_seconds" JsNumber "Timer duration in seconds"
                opt "label" JsString "Timer label"] }

    { Method = "alarm.list_alarms"; ToolName = "pocket_alarm_list"
      Description = "List all set alarms"
      Params = [opt "limit" JsNumber "Maximum alarms to return"] }

    // ── WiFi ─────────────────────────────────────────────────────────────────
    { Method = "wifi.status"; ToolName = "pocket_wifi_status"
      Description = "Get WiFi connection status and current network"
      Params = [] }

    { Method = "wifi.scan"; ToolName = "pocket_wifi_scan"
      Description = "Scan for available WiFi networks"
      Params = [] }

    // ── Bluetooth ────────────────────────────────────────────────────────────
    { Method = "bt.status"; ToolName = "pocket_bt_status"
      Description = "Get Bluetooth adapter status"
      Params = [] }

    { Method = "bt.paired_devices"; ToolName = "pocket_bt_paired"
      Description = "List paired Bluetooth devices"
      Params = [] }

    // ── Storage ──────────────────────────────────────────────────────────────
    { Method = "storage.info"; ToolName = "pocket_storage_info"
      Description = "Get storage space information (total, used, free)"
      Params = [] }

    // ── Download ─────────────────────────────────────────────────────────────
    { Method = "download.enqueue"; ToolName = "pocket_download"
      Description = "Download a file from URL"
      Params = [req "url" JsString "URL to download"
                opt "filename" JsString "Custom filename for downloaded file"] }

    // ── STT ──────────────────────────────────────────────────────────────────
    { Method = "stt.recognize"; ToolName = "pocket_stt_recognize"
      Description = "Start speech-to-text recognition from microphone"
      Params = [opt "language" JsString "Language code (e.g., en-US, zh-CN)"
                opt "max_seconds" JsNumber "Maximum recording duration"] }

    // ── Intent ───────────────────────────────────────────────────────────────
    { Method = "intent.fire"; ToolName = "pocket_intent_fire"
      Description = "Fire an Android intent (open activity, service, etc.)"
      Params = [req "action" JsString "Intent action (e.g., android.intent.action.VIEW)"
                opt "data" JsString "Intent data URI"
                opt "extras" JsString "JSON object of extra parameters"] }

    // ── Media controls ───────────────────────────────────────────────────────
    { Method = "media.now_playing"; ToolName = "pocket_media_now_playing"
      Description = "Get currently playing media info"
      Params = [] }

    { Method = "media.play"; ToolName = "pocket_media_play"
      Description = "Resume media playback"
      Params = [] }

    { Method = "media.pause"; ToolName = "pocket_media_pause"
      Description = "Pause media playback"
      Params = [] }
]

// ── Tool registration ────────────────────────────────────────────────────────

let private defToSpec (def: ToolDef) : ToolSpec = {
    Name            = ToolName def.ToolName
    Description     = def.Description
    Parameters      =
        def.Params
        |> List.map (fun p ->
            p.Name, {
                Type        = p.Type
                Description = p.Description
                Required    = p.Required
            })
        |> Map.ofList
    ConcurrencySafe = false
}

let private defToExecutor (rpc: PocketRpcClient) (def: ToolDef) : Map<string, JsonElement> -> Async<ToolResult> =
    fun args -> async {
        try
            // Build params JSON from tool arguments
            let paramsJson =
                if Map.isEmpty args then "{}"
                else
                    let parts =
                        args
                        |> Map.toList
                        |> List.map (fun (k, v) ->
                            sprintf "\"%s\":%s" k (v.GetRawText()))
                    "{" + String.concat "," parts + "}"

            let! result = rpc.Request(def.Method, paramsJson)
            let text = result.GetRawText()

            // Check if result has ok=false (HostBridge error envelope)
            match result.TryGetProperty("ok") with
            | true, ok when ok.ValueKind = JsonValueKind.False ->
                let msg =
                    match result.TryGetProperty("error") with
                    | true, e -> e.GetRawText()
                    | _ -> text
                return ToolFailure (ExecutionFailed msg)
            | _ ->
                return ToolSuccess text
        with ex ->
            return ToolFailure (ExecutionFailed $"[pocket] {def.Method} failed: {ex.Message}")
    }

/// Create all pocket tool pairs for registration with AgentDependencies.
/// Returns (ToolSpec × executor) list compatible with BotSharp's tool map.
let createPocketTools (rpc: PocketRpcClient) : (ToolSpec * (Map<string, JsonElement> -> Async<ToolResult>)) list =
    toolDefs
    |> List.map (fun def -> defToSpec def, defToExecutor rpc def)
