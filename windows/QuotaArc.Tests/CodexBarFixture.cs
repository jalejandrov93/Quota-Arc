namespace QuotaArc.Tests;

/// Sanitized `GET /usage` response from CodexBar CLI v0.60.3 (`codexbar serve`).
/// Shapes are verbatim from a live capture; accounts are replaced with
/// example.com addresses and unknown fields (pace, details) are kept on
/// purpose so the decoder proves it ignores them.
internal static class CodexBarFixture
{
    public const string Usage = """
    [
      { "provider": "claude", "account": null, "source": "oauth",
        "pace": { "primary": { "stage": "farBehind", "expectedUsedPercent": 83, "willLastToReset": true } },
        "usage": {
          "primary": { "usedPercent": 52, "windowMinutes": 300, "resetsAt": "2026-09-15T19:50:00Z", "resetDescription": "Sep 15 at 2:50PM" },
          "secondary": { "usedPercent": 48, "windowMinutes": 10080, "resetsAt": "2026-09-19T19:00:00Z", "resetDescription": "Sep 19 at 2:00PM" },
          "tertiary": null,
          "extraRateWindows": [
            { "id": "claude-weekly-scoped-fable", "title": "Fable only",
              "window": { "usedPercent": 6, "windowMinutes": 10080, "resetsAt": "2026-09-19T18:59:59Z", "resetDescription": "Sep 19 at 1:59PM" } }
          ],
          "identity": { "providerID": "claude", "loginMethod": "Claude Max 5x", "accountEmail": "user@example.com" },
          "loginMethod": "Claude Max 5x", "updatedAt": "2026-09-15T18:58:45Z" } },

      { "provider": "codex", "account": "user@example.com", "source": "auto",
        "error": { "code": 1, "kind": "provider",
                   "message": "Codex connection failed: failed to fetch codex rate limits: GET https://chatgpt.com/backend-api/wham/usage failed: 401 Unauthorized" } },

      { "provider": "codex", "account": "user2@example.com", "source": "auto",
        "usage": {
          "primary": { "usedPercent": 12.5, "windowMinutes": 300, "resetsAt": "2026-09-15T21:00:00Z" },
          "secondary": { "usedPercent": 30, "windowMinutes": 10080, "resetsAt": "2026-09-20T09:00:00Z" },
          "tertiary": null,
          "identity": { "providerID": "codex", "loginMethod": "plus", "accountEmail": "user2@example.com" },
          "updatedAt": "2026-09-15T18:58:45Z" } },

      { "provider": "opencode", "source": "auto",
        "error": { "code": 1, "kind": "runtime",
                   "message": "Error: selected source requires web support and is only supported on macOS." } },

      { "provider": "opencodego", "source": "local",
        "usage": { "dataConfidence": "estimated",
          "secondary": { "usedPercent": 0, "windowMinutes": 10080, "resetsAt": "2026-09-20T23:59:59Z" },
          "tertiary": { "usedPercent": 0, "windowMinutes": 43200, "resetsAt": "2026-10-05T14:07:45Z" },
          "primary": { "usedPercent": 0, "windowMinutes": 300, "resetsAt": "2026-09-15T23:57:35Z" },
          "updatedAt": "2026-09-15T18:57:35Z" } },

      { "provider": "antigravity", "source": "cli",
        "usage": {
          "tertiary": null,
          "identity": { "providerID": "antigravity" },
          "secondary": { "resetDescription": "You have used some of your weekly limit, it will fully refresh in 1 day, 6 hours.",
                         "resetsAt": "2026-09-17T01:21:11Z", "windowMinutes": 10080, "usedPercent": 98.95788002759218 },
          "extraRateWindows": [
            { "window": { "windowMinutes": 300, "usedPercent": 0, "resetsAt": "2026-09-15T23:58:02Z" },
              "title": "Gemini 5-hour", "id": "antigravity-quota-summary-gemini-5h" },
            { "window": { "windowMinutes": 10080, "usedPercent": 75.50923377275467, "resetsAt": "2026-09-17T18:20:07Z" },
              "title": "Gemini weekly", "id": "antigravity-quota-summary-gemini-weekly" },
            { "window": { "usedPercent": 0, "windowMinutes": 300, "resetsAt": "2026-09-15T23:58:02Z" },
              "title": "Claude/GPT 5-hour", "id": "antigravity-quota-summary-3p-5h" },
            { "window": { "windowMinutes": 10080, "resetsAt": "2026-09-17T01:21:11Z", "usedPercent": 98.95788002759218 },
              "title": "Claude/GPT weekly", "id": "antigravity-quota-summary-3p-weekly" }
          ],
          "updatedAt": "2026-09-15T18:58:04Z",
          "primary": { "resetDescription": "You have used some of your weekly limit, it will fully refresh in 1 day, 23 hours.",
                       "resetsAt": "2026-09-17T18:20:07Z", "windowMinutes": 10080, "usedPercent": 75.50923377275467 } } },

      { "provider": "copilot", "source": "api",
        "pace": { "primary": { "etaSeconds": 3845, "stage": "farAhead" } },
        "usage": { "secondary": null,
          "identity": { "loginMethod": "Individual", "providerID": "copilot" },
          "tertiary": null,
          "primary": { "usedPercent": 99.7, "resetsAt": "2026-10-01T00:00:00Z" },
          "details": [ { "rows": [ { "id": "copilot-seat-credits", "value": "199", "label": "Credits used", "usageValue": 199 } ],
                         "title": "Credits" } ],
          "loginMethod": "Individual", "updatedAt": "2026-09-15T18:59:05Z" } }
    ]
    """;
}
