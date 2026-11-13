# ChatGPT Yesterday

![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-555555)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
[![License: MIT](https://img.shields.io/badge/License-MIT-brightgreen)](LICENSE)

ChatGPT ships updates faster than it fixes bugs. In practice, fixing one thing often breaks three: white screens, crashes, and login spinners that never stop. The version number keeps racing ahead, while the build that actually works is often the one from yesterday.

Until a fix arrives, continuing to use the app usually means hunting down an older installer. This project doesn't fix bugs—it simply retrieves an older installer from the official source.

## Features

Works on Windows, macOS, and Linux. It lists older official releases that are still available for download and lets you narrow them down by platform, architecture, version, and installer format.

Once you select a version, the full download URL is shown first. After you confirm, the file is saved to the current directory.

The tool only downloads files. It doesn't install anything or modify your system, nor does it host installers or provide cracks.

## Usage

### Run a release build

Download the latest release for your platform from the [GitHub Releases page](https://github.com/lianglanzheng/ChatGPT-Yesterday/releases), and run the included executable directly. No .NET SDK installation is required.

### Run from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) version 10.0.300 or later, make sure you're online, and run the following from the project directory in an interactive terminal:

```sh
dotnet run --file ChatGptYesterday.cs
```

Follow the prompts to choose a version. The downloaded file will be saved to the current directory; installing it is up to you.

`ChatGptYesterday.cs` is the entry point. It includes `ChatGptVersions.cs` to fetch the version catalog and `ChatGptDownload.cs` to handle selection and downloading. `response.schema.json` defines the JSON format passed between them.

Windows MSIX filenames don't include a version number. If you want to keep multiple versions, rename the files or put them in separate folders after downloading.
