; Inno Setup script for PasteJump.
;
; Compiled by tools/pack-release.ps1, which passes the version and the paths in - nothing here is
; hard-coded to a version, so the script cannot fall behind Directory.Build.props.
;
;   ISCC.exe /DAppVersion=2026.1.0.0 /DStageDir=... /DOutputDir=... /DRepoRoot=... PasteJump.iss
;
; Two decisions in here are load-bearing and should not be "tidied":
;
;   PrivilegesRequired=lowest  installs per-user, into %LOCALAPPDATA%\Programs\PasteJump. That is not
;     timidity about UAC. PasteJump keeps its clips in a data\ folder BESIDE the executable, and
;     Program Files is not writable - an all-users install would put the app somewhere it cannot store
;     anything, and the first thing the user would meet is the read-only warning. A per-user install
;     keeps the portable data model working and needs no elevation. It also matches the shape of the
;     app: a resident per-user utility that installs a keyboard hook for one logon session.
;
;   AppMutex must match the app's own single-instance mutex. Without it, installing over a running
;     copy fails on a locked PasteJump.exe with an error about a file in use; with it, Inno recognises
;     the running instance and offers to close it. Note the consequence for unattended use: with
;     /SILENT /SUPPRESSMSGBOXES that prompt defaults to Cancel, so setup exits 1 while PasteJump is
;     running. That is correct - it is refusing to replace a file in use - but it means a deployment
;     script has to stop the app first.

#ifndef AppVersion
  #define AppVersion "0.0.0.0"
#endif

#ifndef StageDir
  #define StageDir "..\artifacts\release\stage"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

#ifndef RepoRoot
  #define RepoRoot ".."
#endif

#define AppName "PasteJump"
#define AppPublisher "Lokesh Govindu"
#define AppUrl "https://github.com/lokeshgovindu/PasteJump"

[Setup]
; Never change AppId. It is what Windows uses to recognise an upgrade of the same product rather than
; a second installation, and a new one would leave the old entry in Add/Remove Programs for ever.
AppId={{B4F1D6E2-3A57-4C0E-9E8B-7D2A6F41C935}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user. See the note at the top of this file - this is what keeps data\ beside the exe writable.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no

; The running app locks its own exe and holds this mutex. Both are needed: CloseApplications finds the
; window, AppMutex finds an instance that has no window - which PasteJump never does.
AppMutex=Global\PasteJump.SingleInstance.9F2C41A6
CloseApplications=yes
RestartApplications=no

LicenseFile={#StageDir}\LICENSE.txt
SetupIconFile={#RepoRoot}\src\PasteJump.App\Assets\pastejump.ico
UninstallDisplayIcon={app}\PasteJump.exe
UninstallDisplayName={#AppName} {#AppVersion}

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-setup
WizardStyle=modern

; x64 only, matching the published runtime identifier. Without this the installer would run happily on
; ARM64 or 32-bit Windows and drop an exe that cannot start.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; The payload is a single-file bundle that is already compressed internally, so it barely compresses
; again - solid LZMA2 still shaves a few MB off and costs nothing but compile time.
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Checked by default: a clipboard manager that has to be started by hand is not much use, and this is
; a deliberate installation rather than something that arrived by accident.
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The whole staging folder, recursively, because what the installer deploys is a FOLDER publish rather
; than the single-file executable the portable ZIP carries. That is the point of it: single-file spends
; about a second on every launch extracting its bundle and decompressing assemblies before any of our code
; runs - 1,100-1,145 ms measured, against 228 ms for a folder build - and it buys nothing here, since an
; installer is putting files in a directory anyway. Someone who ran setup does not care that the
; directory holds 200 files; they do notice a second of nothing after clicking the shortcut.
;
; It costs disk: about 143 MB installed against 65 MB. Deliberate.
;
; One Source line rather than a list, so a file the build starts or stops producing cannot be missed. The
; staging folder is assembled by tools/pack-release.ps1 and holds the publish output plus the manual, the
; README and the licence.
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\PasteJump.exe"
Name: "{group}\{#AppName} Help"; Filename: "{app}\PasteJump.chm"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\PasteJump.exe"; Tasks: desktopicon

; Deliberately the same file the app manages itself: Settings, System, "Start PasteJump when I sign in"
; creates and deletes %APPDATA%\...\Startup\PasteJump.lnk. Writing the identical path means the app's
; own checkbox reflects what the installer did and can turn it off again - a differently named shortcut
; here would give the user two switches for one behaviour, one of which appears not to work.
Name: "{userstartup}\{#AppName}"; Filename: "{app}\PasteJump.exe"; WorkingDir: "{app}"; Comment: "PasteJump clipboard manager"; Tasks: startup

[Run]
Filename: "{app}\PasteJump.exe"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent
Filename: "{app}\PasteJump.chm"; Description: "Open the manual"; Flags: shellexec nowait postinstall skipifsilent unchecked

; There is deliberately no [UninstallDelete] section, and the omission is the fix for a bug this script
; shipped with for about ten minutes.
;
; It had:  Type: files; Name: "{userstartup}\{#AppName}.lnk"
;
; on the reasoning that the startup shortcut should go with the app whoever created it. That deletes the
; file unconditionally - including when setup never created it. Tested on a machine that also had a
; PORTABLE copy of PasteJump set to run at logon, and uninstalling wiped that copy's startup entry. The
; two share a path by design (see the [Icons] note above), which is exactly why one must not delete the
; other's.
;
; Inno already removes icons it created, logged per install, so the entry was redundant as well as
; harmful. The remaining gap is deliberate: if the task was left unchecked and the app's own setting
; created the shortcut afterwards, uninstalling leaves it behind pointing at a deleted exe. That is a
; visible, single, easily-deleted file - a far better failure than silently breaking another
; installation's autostart.
;
; Nothing here removes the user's clips, history or settings either. Uninstalling an application is not
; consent to delete the data it was keeping, and a portable install can hold years of history in data\
; beside the exe. The uninstall message says where it is so it can be deleted by hand.

[Messages]
; Inno's default is about "the program", which is not the question here: PasteJump is a background
; utility and the interesting question after uninstalling is where the clipboard history went.
ConfirmUninstall=Remove %1?%n%nYour clips, history and settings are NOT removed. They are in the data folder beside the program, or under %%LOCALAPPDATA%%\PasteJump if you moved them there.
