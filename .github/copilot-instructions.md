# Code & Documentation Style Instructions

## Microsoft Foundry — New Resource Model

This repo uses the **new Microsoft Foundry** resource model (Nov 2025+, preview).

- Resource type: `Microsoft.CognitiveServices/accounts` with `kind: AIServices` and `allowProjectManagement: true`
- Projects: `Microsoft.CognitiveServices/accounts/projects` (child resource — no ML Hub, no Storage Account dependency)
- Capability hosts: `Microsoft.CognitiveServices/accounts/capabilityHosts` and `accounts/projects/capabilityHosts`
- Model deployments: `Microsoft.CognitiveServices/accounts/deployments`
- API version: `2025-04-01-preview` or later
- Endpoint format: `https://<name>.services.ai.azure.com/api/projects/<project>`

Do NOT use the legacy ML Workspaces Hub/Project pattern (`Microsoft.MachineLearningServices/workspaces` with kind Hub/Project). That is Azure AI Studio, not Microsoft Foundry.

Reference: https://github.com/azure-ai-foundry/foundry-samples/tree/main/infrastructure/infrastructure-setup-bicep

---

## Core Principle
Write like a human developer, not an LLM. Avoid patterns that signal AI generation.

---

## Language & Vocabulary

### Banned Overused AI Words
Avoid these except when technically necessary:
- delve, intricate, meticulous, meticulously, navigating, complexities, realm
- showcasing, tailored, towards, underpins, everchanging, ever-evolving, the world of
- not only, but also, diving, unlock, unveil, unveiling, pivotal, crucial
- plays a role, serves as a, offers a, embodies, landscape (metaphorical use)
- bespoke, robust, comprehensive, moreover, furthermore, tapestry, testament
- In today's, It's important to note, It's worth noting

### Write Naturally
- Use simple copulas: prefer "is/are/was" over "serves as," "marks," "represents"
- Vary sentence structure and length naturally
- Allow short, direct sentences
- Don't avoid repeating a word if it's the clearest choice

---

## Content Patterns to Avoid

### No Superficial Hype
❌ "This represents a pivotal moment in..."
❌ "The significance of X cannot be overstated..."
❌ "...marking a transformative shift in..."
❌ "...underscoring the importance of..."
✅ State facts directly without editorializing importance

### No Generic Analysis
❌ Adding "-ing" clauses that interpret significance: "...highlighting its role," "showcasing the power of," "emphasizing the need for"
❌ "Despite its challenges, [subject] continues to thrive..."
❌ "Future prospects include..." sections
✅ Stick to concrete, specific information

### No Fake Ranges
❌ "from performance to security" (not a real spectrum)
❌ "from databases to authentication" (random list, not a range)
✅ Only use "from...to" for actual numerical/temporal/qualitative scales

### No Rule-of-Three Padding
❌ "scalable, maintainable, and efficient architecture"
❌ "powerful, flexible, and intuitive API"
✅ Be specific: "architecture that handles 10k req/s" or just use one adjective

### No Promotional Language
❌ "seamlessly integrates," "effortlessly handles," "elegantly solves"
❌ "cutting-edge," "revolutionary," "game-changing"
❌ "nestled within," "boasts," "proudly features"
✅ Technical descriptions only

### No Vague Attribution
❌ "Experts suggest..." "It is widely considered..." "Many developers find..."
❌ "This approach is considered best practice..."
✅ Either cite a specific source or state it as our decision

---

## Code Comments & Documentation

### No Emojis
❌ Never use emojis in code, comments, commit messages, or documentation
❌ No: "Fix bug 🐛", "Add feature ✨", "Update docs 📝"
❌ No: "// TODO: Refactor this 🔧"
✅ Plain text only

### Be Concise & Specific
- Explain *why* and *context*, not what (code shows what)
- Use concrete examples from this codebase, not hypothetical "a user might..."
- No meta-commentary: "This function...", "The following code...", "As shown above..."

### Match Existing Patterns
- Use the same terminology, naming conventions, and comment style as the rest of the repo
- Don't introduce generic boilerplate that doesn't match the project voice

### Commit Messages
- Start with verb: "Fix bug where...", "Add endpoint for...", "Refactor auth to..."
- Be specific about what changed and why
- No: "Update file", "Improvements", "Refactor code"
- No emojis or special characters

---

## Structural Patterns to Avoid

### No Rigid Outlines
Avoid formulaic "Challenges / Future Prospects / Significance" sections unless genuinely needed

### No False Parallelism
❌ "Not only does it X, but it also Y"
❌ "It's not just about X, it's about Y"
✅ Direct statements

### No List Bloat
- Lists of similar items should use consistent structure
- Don't force everything into lists; use prose when appropriate
- No "See Also" sections with overly broad/vague links

---

## Tone & Voice

### Project-Specific
- Technical, direct, no marketing speak
- Assume reader is competent; don't over-explain basics
- Be opinionated when appropriate (this is our codebase)

### Avoid Over-Hedging
❌ "might potentially possibly could be considered..."
✅ "may reduce latency" or just "reduces latency"

### Consistency
- Maintain same formality level throughout a file
- Don't randomly shift between casual and academic

---

## Final Check
Before submitting code/docs, scan for:
1. Any words from the banned list?
2. Any emojis?
3. Generic/promotional language?
4. Fake significance claims or "-ing" interpretations?
5. Does this sound like *our* code or like a chatbot?

If it reads like a Medium blog post or marketing copy, revise it.