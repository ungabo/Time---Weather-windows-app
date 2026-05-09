# Weather Clock

A borderless Windows desktop application that combines a real-time clock with current weather conditions and forecasts. The app displays weather from NOAA using your location zip code and email.

## Features

- **Real-time Clock**: Large, high-quality display of current time with AM/PM indicator and date
- **Current Weather**: Live temperature and weather condition with large icons
- **Feels Like Temperature**: Display of wind-chill or heat-index adjusted temperature
- **Today's High/Low**: Current day temperature range
- **Hourly Forecast**: 6-hour breakdown with temperature and weather icons
- **7-Day Forecast**: Week-long outlook with highs/lows and conditions
- **Multi-Monitor Support**: Maximize on primary or secondary monitors without disappearing
- **System Tray**: Minimize to system tray and restore from notification area
- **Customizable**: Set email and zip code for weather location via settings dialog

## Requirements

- Windows 7 or later (with .NET Framework 4.0+)
- Internet connection for weather data (NOAA API)
- Valid email address (required for NOAA weather requests)
- Valid U.S. zip code for location lookup

## Building

### Prerequisites

- Windows machine with .NET Framework 4.0+ installed
- PowerShell 5.0+

### Build Process

Run the build script from the project root:

```powershell
.\build.ps1
```

The script will:
1. Compile `WeatherClockApp.cs` using the system C# compiler
2. Embed weather icons and zip code lookup data
3. Apply the manifest for DPI awareness
4. Produce an executable: `dist\Weather Clock.exe`

### Output

The compiled executable is available at:
```
dist/Weather Clock.exe
```

## Usage

1. **First Run**: On first launch, a settings dialog appears requesting:
   - Email address (for NOAA API calls)
   - At least one saved city via zip code
   - A selected default city

2. **Normal Operation**: 
   - The app displays current time, weather, and forecasts
   - Weather updates automatically every 30 minutes
   - Click and drag the title area to move the window
   - Use the window control buttons (minimize, maximize, close)
   - Right-click the system tray icon when minimized to restore or exit

3. **Settings**:
   - Click the settings icon (gear) in the top-right to add/remove cities or change email
   - The app remembers your settings between sessions

## Recent Improvements (May 2026)

### UI/Layout
- Enlarged current weather icon to fill more space in the top-left
- Increased "Feels like" text size for better readability
- Made time display 30% larger while keeping date appropriately positioned
- Enlarged 7-day forecast weather icons for better visibility
- Improved text rendering quality on large numerals (time and temperature)

### Bug Fixes
- Fixed maximize behavior on secondary monitors (previously window would disappear)
- Prevents high/low temperature overlap with main temperature on larger screens
- Date no longer overlaps with hourly forecast row
- Condition text properly positioned below icon without overlap

### Technical
- Multi-monitor aware maximize implementation using monitor bounds detection
- Anti-aliased text rendering for smoother appearance
- Automatic clock/date fit calculation to prevent clipping at any window size

## Architecture

- **Single-file desktop app**: `WeatherClockApp.cs` contains all UI, logic, and data handling
- **Windows Forms**: UI rendered with GDI+ for custom borderless window styling
- **Async weather fetching**: Weather updates run on background thread pool
- **Embedded resources**: Weather icons and zip code database bundled in executable
- **JSON parsing**: Uses built-in `JavaScriptSerializer` for NOAA API responses

## Configuration

Settings are stored in the user's local application data folder:
```
%APPDATA%\Weather Clock\settings.json
```

This file persists:
- Email address
- Saved cities and zip codes
- Currently selected city

## Troubleshooting

### Weather not loading
- Check internet connection
- Verify email address is valid (NOAA requires this for rate limiting)
- Ensure zip code is a valid U.S. code in the database
- Check Windows firewall allows the application internet access

### Window appears off-screen after maximize
- Try dragging the window by the title bar back into view
- Use Escape key to minimize
- Restore from system tray and try again

### Text appears cut off
- Resize the window larger; text scales dynamically to fit available space
- Minimum supported window size is 760×430 pixels

## Dependencies

- **System.Drawing**: GDI+ graphics rendering
- **System.Windows.Forms**: Desktop UI framework
- **System.Web.Extensions**: JSON serialization
- **System.Net**: HTTP requests to weather API
- **NOAA Weather API**: Free weather data (requires email)

## License

[Specify license as needed]

## Author

[Your Name]

---

For issues or feature requests, please refer to the project repository.
