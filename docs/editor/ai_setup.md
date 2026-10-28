# Connect an AI assistant

Open the editor's **AI** dock and choose **Set up AI**, or search F3 for
**AI: setup**. Setup is optional. It appears only while
Editor Settings > GUO > AI > Enabled is on; manual authoring stays available.
An editor launched with `-- --guo-ai-setup` opens the same window directly.

The resizable setup window walks through choosing an assistant, detecting its
adapter, installing/signing in if needed, and checking an ACP session. It never
installs software or sends a model prompt automatically. Copy a command to run
it yourself, or follow **Setup guide**. A changed PATH may require restarting
the editor.

| Assistant | Subscription route |
|---|---|
| OpenAI / Codex | Codex ACP adapter, using its ChatGPT sign-in method. OpenAI API keys are a separate billing route. |
| Claude | Claude ACP adapter, using the Claude Code login and an eligible Claude plan. |
| Google Gemini | Google's official Antigravity ACP server (`agy_acp_server`), using Google account sign-in. The Antigravity CLI (`agy`) is a different program. |
| OpenCode | `opencode acp`, using the Go plan or supported provider configured inside OpenCode. Check the selected provider's billing before sending a message. |

For OpenCode, choose ChatGPT Plus/Pro browser sign-in to connect that plan.
An existing OpenCode Go plan uses its own key: in the Console, open **Settings >
your account name > Create**, then paste the key into OpenCode's terminal.
Console browser sign-in connects a separate provider; it does not connect Go.
The workspace Keys page may offer service-account keys instead. See the
[OpenCode Go onboarding issue](https://github.com/anomalyco/opencode/issues/56498).
Claude subscription sign-in uses the Claude adapter, following
[OpenCode's provider guidance](https://opencode.ai/docs/providers/#anthropic).

Google ended individual Google AI subscription access through legacy Gemini CLI
on June 18, 2026. GUO retains that preset in Agents for other supported account
types, but setup directs individual users to Antigravity. See
[Google's migration notice](https://developers.google.com/gemini-code-assist/docs/deprecations/code-assist-individuals)
and the [official ACP distribution registry](https://github.com/agentclientprotocol/registry/tree/main/antigravity-acp).

**Check connection** starts the selected adapter and requests an ACP session.
When an adapter requires authentication, setup shows its offered non-API sign-in
methods. Select the appropriate method and choose **Sign in with selected
method**. Complete any browser/terminal flow the adapter opens. GUO receives
no subscription password. Terminal sign-in commands remain available as a fallback.
During an explicit Google sign-in, GUO opens the adapter's Google Accounts URL
and offers **Open sign-in page** if the browser does not appear. The URL is
kept only for that pending action and is omitted from the transcript.

Setup refuses subscription checks if the editor inherited a relevant API-key or
cloud-billing environment override. It names the problem without displaying any
value. Restart the editor with the intended environment, or explicitly use the
**Services** tab for API/local service configuration. Setup does not change your
system environment, saved provider routing, subscriptions or billing settings.

**Session ready** proves that the adapter initialized and accepted a session.
It does not prove a model response, subscription entitlement or MCP tool use.
**Finish setup and open Chat** becomes available only for a currently ready
session. Your first message is a separate action and consumes the selected
provider's allowance. A server's unsupported-client response is shown as a
migration problem rather than an invitation to repeat sign-in.

Until completion the dock's button says **Set up AI**. After completion it says
**AI setup…**, so another assistant can be added or a connection repaired.
Closing the window leaves setup available. Disabling editor AI cancels the
shared AI lifetime and shuts down owned sessions, including pending sign-in.
No setup code is included in exported game clients.
