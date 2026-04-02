@echo off
chcp 65001 >nul
title Claude Client - Setup & Install
color 0A

echo ============================================
echo    Claude Client - Local Server Setup
echo ============================================
echo.

:: ── Check Admin ──
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [!] Please run this script as Administrator.
    echo     Right-click ^> Run as administrator
    pause
    exit /b 1
)

:: ── Check if Ollama is installed ──
where ollama >nul 2>&1
if %errorlevel% equ 0 (
    echo [OK] Ollama is already installed.
    goto :pull_model
)

:: ── Install Ollama ──
echo [1/3] Downloading Ollama installer...
echo.

set "OLLAMA_INSTALLER=%TEMP%\OllamaSetup.exe"

:: Download using PowerShell
powershell -Command "& { $ProgressPreference = 'SilentlyContinue'; Invoke-WebRequest -Uri 'https://ollama.com/download/OllamaSetup.exe' -OutFile '%OLLAMA_INSTALLER%' }"

if not exist "%OLLAMA_INSTALLER%" (
    echo [ERROR] Failed to download Ollama installer.
    echo         Please download manually from: https://ollama.com/download
    pause
    exit /b 1
)

echo [2/3] Installing Ollama...
echo       (Follow the installer window if it appears)
echo.
start /wait "" "%OLLAMA_INSTALLER%"

:: Verify installation
timeout /t 3 /nobreak >nul
where ollama >nul 2>&1
if %errorlevel% neq 0 (
    :: Try adding to PATH
    set "PATH=%PATH%;%LOCALAPPDATA%\Programs\Ollama"
    where ollama >nul 2>&1
    if %errorlevel% neq 0 (
        echo [ERROR] Ollama installation may have failed.
        echo         Try restarting your terminal and running this script again.
        pause
        exit /b 1
    )
)

echo [OK] Ollama installed successfully!
echo.

:pull_model
:: ── Start Ollama service ──
echo [3/3] Starting Ollama service...
start /b ollama serve >nul 2>&1
timeout /t 3 /nobreak >nul

:: ── Pull model ──
echo.
echo ============================================
echo   Choose a model to download:
echo ============================================
echo.
echo   [1] llama3.1:8b       (4.7 GB) - Good balance
echo   [2] llama3.2:3b       (2.0 GB) - Fast, lighter
echo   [3] mistral:7b        (4.1 GB) - Strong general
echo   [4] codellama:7b      (3.8 GB) - Code-focused
echo   [5] gemma2:9b         (5.4 GB) - Google's model
echo   [6] qwen2.5:7b        (4.4 GB) - Multilingual
echo   [7] deepseek-coder-v2:16b (8.9 GB) - Best for code
echo   [8] Skip (I'll pull a model later)
echo.
set /p MODEL_CHOICE="Enter choice [1-8]: "

if "%MODEL_CHOICE%"=="1" set "MODEL_NAME=llama3.1:8b"
if "%MODEL_CHOICE%"=="2" set "MODEL_NAME=llama3.2:3b"
if "%MODEL_CHOICE%"=="3" set "MODEL_NAME=mistral:7b"
if "%MODEL_CHOICE%"=="4" set "MODEL_NAME=codellama:7b"
if "%MODEL_CHOICE%"=="5" set "MODEL_NAME=gemma2:9b"
if "%MODEL_CHOICE%"=="6" set "MODEL_NAME=qwen2.5:7b"
if "%MODEL_CHOICE%"=="7" set "MODEL_NAME=deepseek-coder-v2:16b"
if "%MODEL_CHOICE%"=="8" goto :write_config

if not defined MODEL_NAME (
    echo Invalid choice. Defaulting to llama3.2:3b
    set "MODEL_NAME=llama3.2:3b"
)

echo.
echo Downloading %MODEL_NAME% ...
echo (This may take several minutes depending on your internet speed)
echo.
ollama pull %MODEL_NAME%

if %errorlevel% neq 0 (
    echo [ERROR] Failed to pull model. Check your internet connection.
    pause
    exit /b 1
)

echo.
echo [OK] Model %MODEL_NAME% downloaded successfully!

:write_config
:: ── Write config for C++ client ──
echo.
echo Writing configuration...

set "CONFIG_DIR=%APPDATA%\ClaudeClient"
if not exist "%CONFIG_DIR%" mkdir "%CONFIG_DIR%"

:: Write local server config
(
echo {
echo   "apiKey": "ollama",
echo   "model": "%MODEL_NAME%",
echo   "maxTokens": 4096,
echo   "temperature": 0.7,
echo   "systemPrompt": "You are a helpful AI assistant.",
echo   "serverMode": "local",
echo   "localEndpoint": "http://localhost:11434"
echo }
) > "%CONFIG_DIR%\settings.json"

echo [OK] Configuration saved to %CONFIG_DIR%\settings.json

:: ── Verify Ollama is running ──
echo.
echo Verifying Ollama server...
timeout /t 2 /nobreak >nul
powershell -Command "try { $r = Invoke-WebRequest -Uri 'http://localhost:11434/api/tags' -UseBasicParsing -TimeoutSec 5; Write-Host '[OK] Ollama server is running on port 11434' } catch { Write-Host '[WARN] Ollama server not responding. Run: ollama serve' }"

:: ── Done ──
echo.
echo ============================================
echo   Setup Complete!
echo ============================================
echo.
echo   Ollama server: http://localhost:11434
if defined MODEL_NAME echo   Model: %MODEL_NAME%
echo   Config: %CONFIG_DIR%\settings.json
echo.
echo   Next steps:
echo   1. Open ClaudeClient.sln in Visual Studio
echo   2. Build and Run (Ctrl+F5)
echo   3. The app will connect to your local Ollama server
echo.
echo   To start Ollama manually:  ollama serve
echo   To list models:            ollama list
echo   To pull more models:       ollama pull ^<model^>
echo.
pause
