# Teams Bot Capabilities Guide

> Teams features and capabilities used in the Contoso Multi-Agent Teams Hub

---

## Table of Contents

- [Personal Bot Scope](#personal-bot-scope)
- [Bot Commands](#bot-commands)
- [Streaming Responses](#streaming-responses)
- [Adaptive Cards](#adaptive-cards)
- [File Upload & Image Handling](#file-upload--image-handling)
- [Emoji Reactions](#emoji-reactions)
- [Welcome Message on Install](#welcome-message-on-install)
- [Deep Links (Future)](#deep-links-future)
- [supportsFiles Capability](#supportsfiles-capability)

---

## Personal Bot Scope

This bot operates in the **personal** scope — it's a 1:1 chat between the user and the bot. This is configured in the Teams manifest:

```json
{
  "bots": [{
    "botId": "${{BOT_ID}}",
    "scopes": ["personal"],
    "isNotificationOnly": false
  }]
}
```

### Why Personal Scope?

- **Privacy**: Operational and marketing queries may contain sensitive business data
- **Simplicity**: No @mention required — every message goes to the bot
- **Performance**: No need to filter in group conversations
- **Focus**: Each user gets their own conversation thread

### Extending to Team/Group Scope

To add team or group chat support, update the manifest:

```json
{
  "bots": [{
    "scopes": ["personal", "team", "groupChat"]
  }]
}
```

You'll also need to:
1. Enable `RemoveRecipientMention` (already set in `appsettings.json`)
2. Handle the bot being @mentioned vs. other messages in the group
3. Consider thread context — use `turnContext.Activity.Conversation.Id` for per-thread agent sessions

---

## Bot Commands

Commands provide a discoverable UI in the Teams compose box. When users type `/`, Teams shows a popup with available commands.

### Registered Commands

| Command | Description | Routes To |
|---------|-------------|-----------|
| `/ops` | Ask the Ops Agent about store performance, metrics, and operations | Ops Agent |
| `/menu` | Ask the Menu Agent for marketing ideas and menu suggestions | Menu Agent |
| `/help` | Show available commands and usage information | Help text |

### Manifest Configuration

```json
{
  "commandLists": [{
    "scopes": ["personal"],
    "commands": [
      { "title": "/ops", "description": "Ask the Ops Agent about store performance..." },
      { "title": "/menu", "description": "Ask the Menu Agent for marketing ideas..." },
      { "title": "/help", "description": "Show available commands and usage information" }
    ]
  }]
}
```

### How Commands Are Parsed

Commands are parsed in `ContosoAgent.ParseCommand()`:

```csharp
// 1. Check for prefix commands (highest priority)
if (text.StartsWith("/ops")) → AgentType.Ops, strip prefix
if (text.StartsWith("/menu")) → AgentType.Menu, strip prefix
if (text.StartsWith("/help")) → AgentType.Help

// 2. No prefix → keyword-based auto-routing
DetermineAgentFromContent(text) → score OpsKeywords vs MenuKeywords
```

The message text after the prefix is forwarded to the agent. If only the prefix is typed (e.g., just `/ops`), a default prompt is used: *"What can I help you with regarding store operations?"*

---

## Streaming Responses

This bot uses the Microsoft 365 Agents SDK's **built-in streaming** to deliver responses progressively — tokens appear in the Teams chat as they're generated, providing a ChatGPT-like experience.

### How It Works

The SDK provides three streaming primitives:

1. **`QueueInformativeUpdateAsync(text)`** — Shows a "thinking" indicator above the message
2. **`QueueTextChunk(text)`** — Queues a text fragment to be sent to the client
3. **`EndStreamAsync()`** — Finalizes the message and makes it permanent

### Implementation in ContosoAgent.cs

```csharp
// Show "thinking" indicator
await turnContext.StreamingResponse.QueueInformativeUpdateAsync(
    $"Contacting the {agentLabel} Agent…", cancellationToken);

// Stream chunks from Foundry agent
await foreach (var chunk in chunks.WithCancellation(cancellationToken))
{
    if (!string.IsNullOrEmpty(chunk))
    {
        turnContext.StreamingResponse.QueueTextChunk(chunk);
    }
}

// Finalize
await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
```

### SDK Configuration

Streaming is enabled by the `AgentApplication` configuration in `appsettings.json`:

```json
{
  "AgentApplication": {
    "StartTypingTimer": false,
    "RemoveRecipientMention": true,
    "NormalizeMentions": true
  }
}
```

`StartTypingTimer: false` is used because the streaming indicators replace the standard typing timer.

### User Experience

```
┌──────────────────────────────────────┐
│ User: /ops How did Southwest do?     │
│                                      │
│ Bot: [Contacting the Ops Agent…]     │  ← Informative update
│                                      │
│ Bot: 📈 **Southwest Region           │  ← Streaming (progressive)
│ Performance**                        │
│                                      │
│ - **Revenue**: $3.2M (+5.2% YoY)    │
│ - **Speed of Service**: 3.1 min      │
│ ...                                  │
│                                      │
│ ┌────────────────────────────────┐   │
│ │ 📊 Operations Dashboard       │   │  ← Adaptive Card follow-up
│ │ Revenue: $3.2M                 │   │
│ │ Speed of Service: 3.1 min      │   │
│ └────────────────────────────────┘   │
└──────────────────────────────────────┘
```

---

## Adaptive Cards

The bot generates [Adaptive Cards](https://adaptivecards.io/) as follow-up messages after streaming completes. Cards provide a structured visual summary of the agent's response.

### Card Types

#### Operations Dashboard (Ops Agent)

Generated when the Ops Agent response contains extractable metrics. Uses a `FactSet` layout:

```
┌─────────────────────────────────────┐
│ 📊  Operations Dashboard            │
│     Contoso Restaurants · Jan 15     │
│─────────────────────────────────────│
│ Revenue        $3.2M (+5.2% YoY)   │
│ Speed of Svc   3.1 minutes          │
│ Order Accuracy  94.3%               │
│ CSAT            4.4/5               │
│─────────────────────────────────────│
│          🤖 Powered by Ops Agent    │
└─────────────────────────────────────┘
```

#### Creative Brief (Menu Agent)

Generated when the Menu Agent response contains a named concept. Uses a branded layout:

```
┌─────────────────────────────────────┐
│ 🍔  The Blaze Box                    │
│     Contoso Burger                   │
│─────────────────────────────────────│
│ A $12.99 family bundle featuring     │
│ our signature flame-grilled patties  │
│                                      │
│ ✅ Limited-time pricing drives      │
│    urgency                           │
│ ✅ Family positioning for weekend   │
│    dinner occasions                  │
│ ✅ Cross-sells sides and drinks     │
│─────────────────────────────────────│
│    🤖 Powered by Menu & Marketing   │
└─────────────────────────────────────┘
```

### Card Schema Version

Cards use Adaptive Card schema **v1.5**, which is widely supported across Teams clients (desktop, web, mobile).

### When Cards Are Sent

Cards are **not** embedded in the streamed message — they're sent as a separate `SendActivityAsync` call after `EndStreamAsync()`. This is because the streaming protocol finalizes the message content before cards can be attached.

```csharp
// After streaming ends:
var fullResponse = responseBuilder.ToString();
var card = _cardService.TryBuildCard(fullResponse, agentLabel);
if (card != null)
{
    var cardActivity = MessageFactory.Attachment(card);
    await turnContext.SendActivityAsync(cardActivity, cancellationToken);
}
```

---

## File Upload & Image Handling

The bot supports image uploads from Teams. Users can paste, drag-and-drop, or attach images to their messages.

### Supported Image Types

Any `image/*` MIME type (PNG, JPEG, GIF, WebP, etc.)

### How Images Are Processed

1. **Download**: 4-layer fallback (see [Architecture — Image Handling Pipeline](ARCHITECTURE.md#image-handling-pipeline))
2. **Analyze**: GPT vision model describes the image in 2–3 sentences
3. **Augment**: Description is appended to the user's message text
4. **Route**: The augmented message is sent to the appropriate agent

### Example Flow

```
User uploads a photo of a restaurant inspection report with text: "Any issues here?"

Bot internally sees:
  "Any issues here?

   [Attached image analysis: The image shows a food safety inspection form
   for Store #1234 with several checkboxes. Three items are marked as
   non-compliant: grease trap maintenance, hand sanitizer stations, and
   temperature log documentation.]"

→ Keyword "inspection" routes to Ops Agent
→ Ops Agent responds with specific food safety guidance
```

### Manifest Configuration

The `supportsFiles` flag in the manifest enables file upload UI in the Teams compose box:

```json
{
  "bots": [{
    "supportsFiles": true
  }]
}
```

---

## Emoji Reactions

The bot responds contextually when users react to messages with emojis. This provides a lightweight feedback mechanism.

### Supported Reactions

| Reaction | Response |
|----------|----------|
| 👍 Like | "Thanks! Glad the {Agent} answer about '{query}' was helpful!" |
| ❤️ Heart | "Appreciate the love! Let me know if you need anything else." |
| 😄 Laugh | "Happy to entertain! Anything else I can help with?" |
| 😮 Surprised | "Hope that was a good surprise!" |
| 😢 Sad | "Sorry that wasn't useful. Try rephrasing your question..." |
| 😠 Angry | "I'm sorry the response wasn't helpful. Please rephrase..." |

### Context-Aware Responses

When a user reacts to a bot response, the bot looks up the original query context:

```csharp
// Store context when streaming completes
var activityId = turnContext.StreamingResponse.FinalMessage?.Id;
_messageContext[activityId] = (userMessage, agentLabel);

// Later, when reaction arrives
var replyToId = turnContext.Activity.ReplyToId;
_messageContext.TryGetValue(replyToId, out var ctx);
// → ctx.Query = "How did Southwest do?", ctx.Agent = "Ops"
```

This allows the reaction response to reference the original question:
> "👍 Thanks! Glad the Ops Agent answer about 'How did Southwest do?' was helpful!"

### Implementation

Reactions are handled via `ActivityTypes.MessageReaction` in the `OnReactionsAddedAsync` handler, registered in the constructor:

```csharp
OnActivity(
    ActivityTypes.MessageReaction,
    OnReactionsAddedAsync);
```

---

## Welcome Message on Install

When a user installs the bot, they receive a welcome message with instructions:

```
👋 Welcome to the Contoso Restaurants Agent Hub!

I can connect you with two specialized AI agents:

🔧 Ops Agent — store operations, KPIs, sales data, food safety
  → Type /ops <your question> or just ask an ops-related question

🍔 Menu & Marketing Agent — campaigns, menu innovation, creative content
  → Type /menu <your question> or just ask a marketing question

💡 Type /help at any time to see this message again.
```

### Implementation

The welcome message is triggered by the `ConversationUpdateEvents.MembersAdded` event:

```csharp
OnConversationUpdate(
    ConversationUpdateEvents.MembersAdded,
    OnMembersAddedAsync);
```

The handler filters out the bot's own member-added event:
```csharp
if (member.Id == turnContext.Activity.Recipient.Id)
    continue; // Skip the bot itself
```

---

## Deep Links (Future)

Deep links allow external surfaces (tabs, cards, emails) to open a bot conversation with pre-filled text. This is a future consideration for the template.

### Potential Use Cases

- **Tab → Bot**: A hub tab with chips that deep-link to `/ops` or `/menu` queries
- **Adaptive Card actions**: "Ask Follow-up" buttons that open a new query
- **Email notifications**: Links that open Teams and start a bot conversation

### Deep Link Format

```
https://teams.microsoft.com/l/chat/0/0?users=28:{BOT_APP_ID}&message=/ops%20weekly%20summary
```

### Implementation Notes

- Deep links require the bot's `BOT_APP_ID` (a GUID, not a display name)
- The `message` parameter pre-fills the compose box but doesn't auto-send
- Users still need to press Enter to send the message
- Works in Teams desktop, web, and mobile clients

---

## supportsFiles Capability

The `supportsFiles` flag in the bot manifest enables the file upload button in the Teams compose box when chatting with the bot.

```json
{
  "bots": [{
    "supportsFiles": true
  }]
}
```

### What This Enables

- **Compose box**: Shows a "+" button for attaching files
- **Drag and drop**: Users can drag images into the chat
- **Paste**: Users can paste images from clipboard (Ctrl+V)
- **File picker**: Users can browse OneDrive or local files

### What This Does NOT Enable

- **File storage**: The bot doesn't store files — it processes them in-memory
- **Non-image files**: The bot currently only processes `image/*` types; other file types are ignored
- **Large files**: The vision model has token limits; very large images are base64-encoded and may hit API size constraints

### Extending File Support

To handle non-image files (PDFs, Excel, etc.):

1. Add content type checks in `OnMessageAsync`:
```csharp
var pdfFiles = inputFiles
    .Where(f => f.ContentType == "application/pdf")
    .ToList();
```

2. Add a document processing service similar to `ImageAnalysisService`
3. Extract text content and append to the user's message
