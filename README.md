# TextBlitz

A smart clipboard manager and text snippet expander for Windows, with cloud sync and subscription billing.

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)

## Features

### 📝 Snippets & Templates (Text Blaze-like)
- Create text shortcuts that expand when you type them
- Assign global hotkeys to snippets for instant insertion
- Searchable snippet picker (Ctrl+Shift+Space)
- Template tokens: `{date}`, `{time}`, `{clipboard}`, `{prompt:FieldName}`

### 📋 Advanced Clipboard Manager
- Automatic clipboard history (up to 500 items for Pro)
- Rich text and HTML format support
- Three paste modes: Keep Original, Use Destination, Merge Formatting
- Pin important items, drag to reorder
- Save multi-item lists for later use

### 🔐 Firebase Auth & Cloud Sync
- Sign in with email/password or Google
- Sync snippets and settings across devices
- Secure token storage using Windows DPAPI
- Works offline with local SQLite cache

### 💳 Subscription Billing
- **60-day free trial** — no credit card required
- Pro Monthly: $4.99/month
- Pro Annual: $39.99/year (save 33%)
- Stripe-powered checkout

## Quick Start

### Download Pre-built Binary

1. Go to [GitHub Actions](https://github.com/codewizard1000/TextBlitz/actions)
2. Click the latest successful workflow run
3. Download `TextBlitz-Build` artifact
4. Extract and run `TextBlitz.exe`

### Build from Source

**Requirements:**
- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
# Clone the repository
git clone https://github.com/codewizard1000/TextBlitz.git
cd TextBlitz

# Restore dependencies
dotnet restore src/TextBlitz/TextBlitz.csproj

# Build
dotnet build src/TextBlitz/TextBlitz.csproj --configuration Release

# Run
dotnet run --project src/TextBlitz/TextBlitz.csproj

# Publish (self-contained executable)
dotnet publish src/TextBlitz/TextBlitz.csproj \
  --configuration Release \
  --self-contained true \
  --runtime win-x64 \
  --output ./publish
```

## Firebase Setup

### 1. Create Firebase Project

1. Go to [Firebase Console](https://console.firebase.google.com)
2. Click "Add project" and name it "TextBlitz"
3. Enable Google Analytics (optional)

### 2. Enable Authentication

1. Go to **Build → Authentication**
2. Click "Get started"
3. Enable **Email/Password** provider
4. Enable **Google** provider (optional)

### 3. Create Firestore Database

1. Go to **Build → Firestore Database**
2. Click "Create database"
3. Choose "Start in production mode"
4. Select a region close to your users

### 4. Set Up Security Rules

In Firestore Database → Rules, paste:

```javascript
rules_version = '2';
service cloud.firestore {
  match /databases/{database}/documents {
    // User can read/write their own data
    match /users/{userId} {
      allow read, write: if request.auth != null && request.auth.uid == userId;
      
      // Subcollections
      match /snippets/{snippetId} {
        allow read, write: if request.auth != null && request.auth.uid == userId;
      }
      
      match /settings/{document=**} {
        allow read, write: if request.auth != null && request.auth.uid == userId;
      }
      
      match /subscription/{document=**} {
        allow read: if request.auth != null && request.auth.uid == userId;
        allow write: if false; // Only server/admin can update subscriptions
      }
    }
  }
}
```

Click "Publish".

### 5. Get Firebase Config

1. Go to **Project settings** (gear icon)
2. Under "Your apps", click the web app (or add one)
3. Copy the config values
4. Update `src/TextBlitz/Assets/firebase-login.html` with your values
5. Update `src/TextBlitz/Services/Firebase/FirestoreSyncService.cs`:
   - `ProjectId`: your-project-id
   - `ApiKey`: your-api-key

## Stripe Setup (Optional - for Billing)

### 1. Create Stripe Account

1. Go to [Stripe Dashboard](https://dashboard.stripe.com)
2. Create an account

### 2. Create Products and Prices

1. Go to **Products**
2. Create two products:
   - **Pro Monthly** — $4.99/month
   - **Pro Annual** — $39.99/year
3. Note the Price IDs (they look like `price_abc123`)

### 3. Set Up Stripe Integration

1. Create a backend endpoint that creates Checkout Sessions
2. Update `src/TextBlitz/Assets/stripe-checkout.html`:
   - Replace `STRIPE_PUBLISHABLE_KEY` with your test/live key
   - Replace price IDs in the `plans` object
3. Update `src/TextBlitz/Services/Billing/StripeConfig.cs` with your values

## Usage

### First Launch

1. Run `TextBlitz.exe`
2. The app starts minimized to the system tray
3. Right-click the tray icon for menu options
4. Click "Settings" to sign in or create an account

### Default Hotkeys

| Hotkey | Action |
|--------|--------|
| `Ctrl+Shift+V` | Open clipboard tray |
| `Ctrl+Shift+Space` | Open snippet picker |
| `Ctrl+Shift+L` | Paste last clipboard item |

*Change hotkeys in Settings*

### Creating Snippets

1. Open snippet manager (right-click tray → "Open Snippets")
2. Click "New Snippet"
3. Enter:
   - **Name**: Descriptive name
   - **Content**: What gets inserted
   - **Shortcut**: Text to type (e.g., `sig`)
   - **Hotkey**: Optional keyboard shortcut
4. Click Save

### Template Tokens

Use these in snippet content:

| Token | Output |
|-------|--------|
| `{date}` | Current date (configurable format) |
| `{time}` | Current time |
| `{clipboard}` | Current clipboard content |
| `{prompt:Name}` | Prompts user for input |

Example snippet content:
```
Best regards,
Jon Carrick
{date}
```

### Clipboard Tray

- **History tab**: Recent clipboard items
- **Lists tab**: Saved multi-item collections
- Click any item to paste
- Pin items to keep them permanently
- Drag pinned items to reorder

### Formatting Modes

Choose how content is pasted:

1. **Keep Original** — Preserves rich formatting (RTF/HTML)
2. **Use Destination** — Pastes as plain text
3. **Merge Formatting** — Best-effort merge (beta)

The selected mode persists across restarts.

## Data Storage

Local data is stored in:
```
%LOCALAPPDATA%\TextBlitz\
├── textblitz.db       # SQLite database
├── auth.dat           # Encrypted auth tokens
└── settings.json      # App settings
```

## Troubleshooting

### App won't start

- Ensure .NET 8 Runtime is installed
- Check Windows Event Viewer for errors
- Try running as Administrator

### Firebase auth not working

- Verify Firebase config is correct
- Check Firestore security rules are published
- Ensure Email/Password provider is enabled

### Snippets not expanding

- Check if expansion is enabled in Settings
- Some apps (games, elevated apps) may block keyboard hooks
- Try the global snippet picker hotkey instead

### Hotkeys not working

- Check for conflicts with other apps
- Verify hotkeys are registered in Settings
- Some apps capture hotkeys globally

## Development

### Project Structure

```
TextBlitz/
├── src/TextBlitz/
│   ├── App.xaml.cs              # Application entry point
│   ├── Views/                   # WPF windows (XAML)
│   ├── ViewModels/              # MVVM view models
│   ├── Services/                # Business logic
│   │   ├── Firebase/            # Auth and sync
│   │   ├── Clipboard/           # Clipboard monitoring
│   │   ├── Snippets/            # Text expansion
│   │   ├── Hotkeys/             # Global hotkeys
│   │   └── Billing/             # Subscriptions
│   ├── Models/                  # Data models
│   ├── Data/                    # SQLite database
│   ├── Helpers/                 # Win32 interop
│   └── Assets/                  # HTML pages for WebView2
└── .github/workflows/           # CI/CD
```

### Key Technologies

- **WPF** — UI framework
- **WebView2** — Embedded browser for auth/checkout
- **SQLite + Dapper** — Local database
- **Firebase REST API** — Cloud sync
- **Low-level keyboard hooks** — Snippet expansion
- **Clipboard format listeners** — Clipboard monitoring

### Contributing

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Create a Pull Request

## License

MIT License — see LICENSE file for details.

## Acknowledgments

- Inspired by Text Blaze, Ditto, and ClipX
- Built with .NET, WPF, and Firebase

## Support

- GitHub Issues: [Report bugs or request features](https://github.com/codewizard1000/TextBlitz/issues)
- Email: support@textblitz.app (when available)

---

**Made with ❤️ for keyboard warriors everywhere.**
