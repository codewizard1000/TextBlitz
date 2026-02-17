TextBlitzYou are going to Develop an app for me called TexBlitz. Let's start with a Windows app. We will then later create the MacOS app. me. Create a project on GitHub for me. And execute the plan below. You have leeway on the plan but this should get you started and Let me know what I'm looking for. Once it is completed let me know and let me know how I can install it on my computer.  You should have access to Claude Code via the CLI and you should also have access to Codex. Use any tools you need to complete this project. 1) Single copy/paste AI coding agent prompt (compressed, zero fluff)ROLE: You are a senior Windows desktop engineer. Build a Windows 10/11 MVP tray app in C#/.NET 8 (WPF or WinUI3) called "BlazeClip" with (A) Snippets/Templates expansion + hotkeys and (B) Advanced Clipboard tray. Add Firebase Auth + Firestore sync (snippets/settings/entitlements) and Stripe subscription billing (freemium -> 60-day trial no card -> paywall; $4/mo or $29/yr auto-renew). Prioritize reliability and a clean MVP.

STACK:
- C# .NET 8
- UI: WPF (preferred) + WebView2
- Local persistence: SQLite
- Firebase: Auth + Firestore (use Firebase Web SDK inside WebView2 for login and billing UI; desktop app consumes ID token + refreshes session)
- Billing: Stripe Checkout + subscription; store subscription state in Firestore (use Firebase payments tutorial/Firestore Stripe approach); enforce entitlements locally.
- Packaging: MSIX or simple installer.

CORE FEATURES (MVP):
A) SNIPPETS/TEMPLATES (Text Blaze-like behavior)
1) Snippet CRUD UI:
   - list/search snippets
   - create/edit: name, content, textShortcut, hotkey, enabled flags
   - conflict detection for shortcut/hotkey
   - "record hotkey" control that captures key combo
2) Expansion:
   - If user types textShortcut then delimiter (Space/Tab/Enter/punctuation), replace shortcut with snippet content.
   - If user presses snippet hotkey, insert snippet content immediately.
   - Global "Snippet Picker" hotkey opens searchable palette; selecting inserts snippet.
3) Template tokens (MVP subset):
   - {date} {time} (format configurable)
   - {clipboard} inserts current clipboard plain text
   - {prompt:FieldName} prompts user for value then inserts
4) Insertion method:
   - Remove typed shortcut token then paste/insert expansion into active app. Must work in Notepad and Chrome inputs; best-effort in Word.
   - Use Paste Engine to apply formatting choice when inserting (at least for clipboard pastes; optionally for snippets).

B) ADVANCED CLIPBOARD TRAY
1) Clipboard capture:
   - Watch clipboard changes; store item with plain text always; store rich formats when available (RTF and/or HTML). Store timestamp and best-effort source app.
   - Maintain history up to limit (configurable).
2) Tray UI:
   - System tray icon; left click toggles clipboard tray window. Right click menu: Open Snippets, Settings, Quit.
   - Clipboard Tray window: search, tabs (History / Lists), per-item preview, pin toggle, click-to-paste, multi-select.
3) Formatting Mode selector (sticky persistent):
   - 3 mutually exclusive modes: (1) Keep original formatting (2) Use destination formatting (3) Merge formatting.
   - The selected mode persists across restarts and remains active until user changes it.
   - Implement:
     - Keep original: paste RTF/HTML if available else plain.
     - Destination: paste plain text.
     - Merge: best-effort; if too hard in MVP, treat as Destination but keep UI + setting and label Merge "beta".
4) Pin + drag reorder:
   - Items can be pinned. Pinned items maintain stable user-defined order (drag/drop reorder).
   - Pinned items stay where placed unless dragged or unpinned.
   - Unpinned items default reverse chronological.
5) Saved Lists:
   - User can multi-select clipboard items and "Save as List…".
   - Lists tab shows named lists; clicking a list loads that list's items into the tray.
   - List items stored as snapshots (content copied into list) so they persist even when history rotates.
   - Provide "Back to History".

C) AUTH + SYNC + ENTITLEMENTS (Firebase)
1) Auth:
   - Sign up/in via WebView2 using Firebase Auth (email/password MVP; Google optional).
   - Desktop app stores tokens securely and reads user profile/entitlements from Firestore.
2) Trial:
   - On first sign-up, set trialStart=now and trialEnd=now+60 days in Firestore user doc. No credit card required.
3) Subscription:
   - Plans: Pro Monthly $4, Pro Annual $24. Auto-renew via Stripe subscription.
   - Implement upgrade flow in WebView2: choose plan -> Stripe Checkout -> on success return to app.
   - Subscription state synced to Firestore (active/past_due/canceled). Entitlements derived from Firestore.
4) Gating:
   - Free tier has limits; Trial and Paid unlock all.
   - After trialEnd, if not paid, show upgrade modal and enforce free-tier limits.

D) SETTINGS + HOTKEYS
1) Global hotkeys (customizable):
   - Open clipboard tray default Ctrl+Shift+V
   - Open snippet picker default Ctrl+Shift+Space
   - Paste last clipboard default Ctrl+Shift+L
2) Settings:
   - startup on boot
   - history size limit
   - delimiter triggers for shortcuts
   - default formatting mode (same as tray selector)
   - backup/export (optional MVP: export snippets JSON)

DATA & SYNC SCOPE:
- Firestore sync: snippets + settings + entitlements. Do NOT sync clipboard history in MVP (local only).
- SQLite: store clipboard history, pinned order, lists, and local cache of snippets/settings for offline use.

ACCEPTANCE TESTS (must pass):
1) Sign up -> Firestore user doc created with trialEnd 60 days out.
2) Create snippet with textShortcut and hotkey via UI; expansion works in Notepad and Chrome input.
3) Clipboard capture appears within 1s. Formatting mode persists after restart.
4) Pin 3 items, drag reorder, restart -> order persists.
5) Save a list of 5 items; list loads correctly; items persist after history rotates.
6) Simulate trial expiration -> upgrade prompt shown; free limits enforced.
7) Complete Stripe upgrade -> Firestore shows active subscription -> Pro unlocked.

DELIVERABLES:
- Repo with solution, README setup steps, Firebase project setup notes, Stripe product/price IDs, and local dev instructions.
- A minimal test plan and a demo script.

IMPLEMENTATION NOTES:
- Use SQLite migrations.
- Prefer clean MVVM.
- Be conservative: ship a polished core rather than extra features.