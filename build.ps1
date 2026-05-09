$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$source = Join-Path $root "WeatherClockApp.cs"
$manifest = Join-Path $root "app.manifest"
$assets = Join-Path $root "Assets"
$icon = Join-Path $assets "app.ico"
$output = Join-Path $dist "Weather Clock.exe"

if (!(Test-Path $compiler)) {
    throw "C# compiler not found at $compiler"
}

if (!(Test-Path $icon)) {
    throw "App icon not found at $icon"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

& $compiler `
    "/nologo" `
    "/target:winexe" `
    "/optimize+" `
    "/win32manifest:$manifest" `
    "/win32icon:$icon" `
    "/reference:System.dll" `
    "/reference:System.Core.dll" `
    "/reference:System.Drawing.dll" `
    "/reference:System.Windows.Forms.dll" `
    "/reference:System.Web.Extensions.dll" `
    "/resource:$(Join-Path $assets 'us_zipcodes.tsv'),us_zipcodes.tsv" `
    "/resource:$(Join-Path $assets 'ic_weather_cloudy.png'),ic_weather_cloudy.png" `
    "/resource:$(Join-Path $assets 'ic_weather_fog.png'),ic_weather_fog.png" `
    "/resource:$(Join-Path $assets 'ic_weather_partly_cloudy.png'),ic_weather_partly_cloudy.png" `
    "/resource:$(Join-Path $assets 'ic_weather_rain.png'),ic_weather_rain.png" `
    "/resource:$(Join-Path $assets 'ic_weather_snow.png'),ic_weather_snow.png" `
    "/resource:$(Join-Path $assets 'ic_weather_storm.png'),ic_weather_storm.png" `
    "/resource:$(Join-Path $assets 'ic_weather_sunny.png'),ic_weather_sunny.png" `
    "/resource:$(Join-Path $assets 'ic_weather_wind.png'),ic_weather_wind.png" `
    "/out:$output" `
    "$source"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Built $output"
