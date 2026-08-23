# SMS Backup Viewer

Quick SMS Backup and Restore Viewer/Converter I cooked up in an hour to view and convert the backups. 97% Claude as I only had to fix a few bits of bad code. Use it if you need it. 

**Read your text messages on your computer. Save your photos. Print your conversations.**

If you back up your phone with
[SMS Backup &amp; Restore](https://www.synctech.com.au/sms-backup-restore/), this opens those
backup files on a Windows PC so you can actually read them — search them, save every photo and
video out of them, and turn any conversation into a PDF you can print or hand to someone.

**Your messages never leave your computer.** Nothing is uploaded. Nothing is sent anywhere.
There is no account, no sign-in, and no internet connection required.

---

## Getting started

**1. Download it.** Get the latest version from the [Releases page](../../releases). There are two:

| Download | Size | |
|---|---|---|
| **standalone** | 70 MB | Nothing else to install. **Pick this one.** |
| **needs-dotnet** | 14 MB | Smaller, but you must install [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) first. |

Unzip it anywhere — your Desktop is fine.

**2. Run `SmsBackupViewer.App.exe`.** There is no installer. If Windows shows a blue "Windows
protected your PC" box, click **More info** then **Run anyway** — that appears for any program
without a paid code-signing certificate.

**3. Click "Open backup…"** and choose the XML file your phone created. They are usually named
like `sms-20230611004105.xml` or `calls-20230618004105.xml`, and live in the folder you told the
backup app to use, or in your Google Drive or Dropbox folder.

You can select more than one file at once, or drag files onto the window.

Keep the files from the zip together in one folder.

> **Using Google Drive, OneDrive or Dropbox?** Make sure the file is actually downloaded to your
> PC first — right-click it and choose *Always keep on this device*. Files that exist only in the
> cloud will fail to open, or take a very long time.

---

## What you can do

### Read your messages

Your conversations are listed down the left, newest first. Click one and it opens like a chat —
messages you sent on the right, messages you received on the left, photos shown in place. Calls
appear in the same timeline, so you can see a text conversation and the phone calls around it
together.

Search for words, narrow to a date range, or hide calls to see only texts.

### See absolutely everything

Click any message and the panel on the right shows *every* piece of information your phone stored
about it — the exact timestamp, the phone number, delivery status, and every other field,
including ones this app doesn't try to interpret. Nothing is hidden from you.

### Save all your photos and videos

**Extract media…** pulls every photo, video, voice message and file out of your backups and saves
them into ordinary folders you can browse, copy or back up.

Point it at the folder holding your backups, choose where to save, and it does the rest. Take
everything, or just one person's conversation. Files are named by date so they sort in order, and
you get a spreadsheet listing what came from where.

If you have years of backups, this handles it — it recognises the same photo appearing in backup
after backup and saves it once.

### Turn conversations into a PDF

**Export PDF…** prints a conversation the way it looks on screen — message bubbles, photos
included — into a PDF you can print, email or file away.

Videos and voice messages can't play inside a PDF. Instead each one appears as a labelled entry
saying what it is, and the actual file is saved in a folder next to the PDF. Keep the folder with
the PDF and everything stays together.

Each PDF opens with a summary page recording what is inside it, the dates it covers, and which
backup files it came from. The layout is adjustable — paper size, text size, date format, time
zone, whether to include calls, and more.

---

## Common questions

**Will this change or delete my backups?**
No. Your files are only ever read, never modified. Anything it creates goes where you tell it.

**How big a backup can it handle?**
Very big. Backups of several gigabytes open in seconds, and collections totalling tens of
gigabytes work fine. Files are read in a steady stream rather than loaded into memory, so size is
not really a limit.

**My photos aren't showing.**
Tick **Load images and media** before opening the backup. Without it, messages still appear but
pictures are skipped, which makes very large files open faster.

**Nothing happens when I open the file.**
Check it is the `.xml` file itself and not a `.zip`. If your backup is zipped, unzip it first.

**Can I use it on a Mac or a phone?**
Not currently — this is a Windows program.

**Is it really free?**
Yes. Free, open source, no adverts, no accounts, nothing to buy.

---

## Privacy

This program cannot reach the internet. It contains no networking code at all — no uploads, no
analytics, no update checks, not even fonts loaded from the web. A test in the project fails if
anyone ever adds any.

---

## For developers

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Windows.

```
git clone <repo-url>
cd "SMS Backup & Restore Desktop Viewer And Converter"
dotnet build
dotnet test
dotnet run --project src/SmsBackupViewer.App
```

To produce the two release builds:

```
dotnet publish src/SmsBackupViewer.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/standalone

dotnet publish src/SmsBackupViewer.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/needs-dotnet
```

| Project | What it is |
|---|---|
| `SmsBackupViewer.Core` | Streaming XML parser, conversation grouping, attachment extraction |
| `SmsBackupViewer.App` | The WPF application |
| `SmsBackupViewer.Pdf` | PDF rendering, built on QuestPDF |
| `SmsBackupViewer.Core.Tests` | 55 tests against synthetic fixtures |

The backup format is documented in [docs/xml-format.md](docs/xml-format.md) — how conversations
are grouped by the last eight digits of a number, why group participants must be sorted, how RCS
threads carry their real participants, and why every field is treated as optional. Anyone writing
their own reader will want that first.

Dependencies, all free and permissively licensed:
[QuestPDF](https://www.questpdf.com/) (MIT source, Community licence),
[Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) (MIT),
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT), and the
[Raleway](https://fonts.google.com/specimen/Raleway) and
[Open Sans](https://fonts.google.com/specimen/Open+Sans) fonts (SIL Open Font License), embedded
in the program rather than downloaded.

---

## Licence

[MIT](LICENSE) — use it, change it, share it, sell it. Attribution is the only condition, and
there is no warranty.

## Credits

Thanks to **SyncTech Support** for publishing the
[XML field documentation](https://www.synctech.com.au/sms-backup-restore/fields-in-xml-backup-files/)
and for their help while this was being built.

This is an independent tool. It is not an official SyncTech product and is not endorsed by them.
Please don't send them support requests about it; open an issue [here](../../issues) instead.
