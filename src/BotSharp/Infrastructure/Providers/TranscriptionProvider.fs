module BotSharp.Infrastructure.Providers.TranscriptionProvider

#nowarn "3261"

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers

// ═══════════════════════════════════════════════════════════════════════════
// Audio transcription using OpenAI-compatible Whisper API
//
// Port of nanobot's GroqTranscriptionProvider.
// Supports Groq (whisper-large-v3) and OpenAI (whisper-1) endpoints.
//
// Usage: called from TelegramChannel when voice/audio messages arrive.
// ═══════════════════════════════════════════════════════════════════════════

type TranscriptionConfig = {
    ApiUrl : string    // e.g. "https://api.groq.com/openai/v1/audio/transcriptions"
    ApiKey : string
    Model  : string    // e.g. "whisper-large-v3" or "whisper-1"
}

let defaultGroqConfig (apiKey: string) = {
    ApiUrl = "https://api.groq.com/openai/v1/audio/transcriptions"
    ApiKey = apiKey
    Model  = "whisper-large-v3"
}

let defaultOpenAIConfig (apiKey: string) = {
    ApiUrl = "https://api.openai.com/v1/audio/transcriptions"
    ApiKey = apiKey
    Model  = "whisper-1"
}

/// Transcribe an audio file using Whisper API.
/// Returns the transcribed text, or empty string on failure.
let transcribe
    (httpClient : HttpClient)
    (config     : TranscriptionConfig)
    (filePath   : string)
    : Async<string> =
    async {
        if String.IsNullOrEmpty config.ApiKey then return ""
        elif not (File.Exists filePath) then return ""
        else
            try
                use content = new MultipartFormDataContent()
                let fileBytes = File.ReadAllBytes(filePath)
                let fileContent = new ByteArrayContent(fileBytes)
                fileContent.Headers.ContentType <- MediaTypeHeaderValue("audio/ogg")
                content.Add(fileContent, "file", Path.GetFileName(filePath))
                content.Add(new StringContent(config.Model), "model")

                let req = new HttpRequestMessage(HttpMethod.Post, config.ApiUrl)
                req.Headers.Add("Authorization", $"Bearer {config.ApiKey}")
                req.Content <- content

                let! resp = httpClient.SendAsync(req) |> Async.AwaitTask
                let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

                if resp.IsSuccessStatusCode then
                    use doc = System.Text.Json.JsonDocument.Parse(body)
                    match doc.RootElement.TryGetProperty("text") with
                    | true, text -> return text.GetString() |> Option.ofObj |> Option.defaultValue ""
                    | _ -> return ""
                else
                    eprintfn "[Transcription] API error (%d): %s" (int resp.StatusCode) (if body.Length > 200 then body.[..199] else body)
                    return ""
            with ex ->
                eprintfn "[Transcription] Error: %s" ex.Message
                return ""
    }
