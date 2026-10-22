# How to use AI Export Viewer

A quick guide to reviewing your organisation's Claude export. Everything stays on your computer.

## 1. Install the app

1. Download `AIExportViewer-win-Setup.exe` from the [latest release](https://github.com/jamtheradar/AIExportViewer/releases/latest).
2. Run it. If Windows says "Windows protected your PC", choose **More info**, then **Run anyway**.
3. The app opens. Next time, start it from the Start menu: **AI Export Viewer**.

It updates itself. When a new version is ready you'll see **Update ready** at the top of the window.

## 2. Get the export from Claude

An Owner of your Claude Team or Enterprise organisation asks Claude for a data export in the organisation's settings on
claude.ai (look for **Export data**, under data and privacy). When it's ready, Claude sends a small **manifest** file
(named like `manifest-….json`) with download links for the zip files.

The links usually only work for about 24 hours, and some only work once, so download the files soon.

## 3. Download and open it

In the app, choose **Download an export** on the left, then:

1. **Choose manifest…** and pick the manifest file.
2. Click the download button. The app downloads each zip file from claude.ai and ticks it off.
3. When they're all ticked, open the export from **3. Open the export**.

Already downloaded the zip files yourself? On **Home**, choose **Choose zip files…**, or **Choose export folder…** if
you've unzipped them. A folder can hold several exports (for example one a month) and they're combined.

The first time you open an export the app reads it all, which can take a few minutes for a large one. After that it
opens quickly.

## 4. Look around

- **Overview**: who used Claude, how much, and anything that needs your attention.
- **People**: each person, their conversations and activity.
- **Conversations**: read any conversation as it happened, including files people attached.
- **Projects**, **What Claude remembers** and **Documents & pages**: the other things people made or shared.
- **Search** (or Ctrl+F): find any word or name across the whole export.

**Basic** and **Expert** at the top switch between what a reviewer needs and every detail in the export. Start with
Basic.

## 5. Check for sensitive information

- **Sensitive data**: click **Check now** to look for things like tax file numbers, card numbers, passwords and keys
  that people typed or pasted into Claude. Values are hidden until you click to show them.
- **Watchlists**: add the words you care about, such as client names or project codes, one per line. The app finds
  every conversation that mentions them.

## 6. Flag things and save a report

- Click **⚑ Flag** on a conversation or message to mark it for follow-up, and add a note. **Flags and notes** on the left
  lists everything you've flagged. Flags stay on your computer and never change the export.
- **Save as PDF** or **Save as CSV** saves a report of what you're looking at. Reports hide sensitive values unless you
  tick the box to include them. Keep saved reports somewhere safe: they contain staff data.

## 7. When you've finished

- Close the export from **Home**.
- To remove everything the app has stored on this computer (its copy of the export, your flags and notes, watchlists),
  go to **Settings** and choose **Delete all local data**. Your own export files are never changed or deleted.

## Privacy in short

- The app only goes online to download an export when you ask, and to check for app updates (you can turn that off in
  **Settings**).
- Nothing from your export is ever sent anywhere.
- Your export files are only read, never changed.
