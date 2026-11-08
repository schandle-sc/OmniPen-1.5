; ============================================================================
;  OmniPen 1.5 – Script cài đặt Inno Setup 6
;  Tác giả: Hồ Văn Thuyên - Trường THPT Hoàng Hoa Thám
;
;  Biên dịch:  build.bat  (tự gọi ISCC.exe)
;  Hoặc tay :  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\OmniPen.iss
;
;  Một bộ cài duy nhất cho cả Windows 64-bit và 32-bit:
;  tự chọn bản exe phù hợp với máy lúc cài.
; ============================================================================

#define MyAppName        "OmniPen"
#define MyAppVersion     "1.5"
#define MyAppFullVersion "1.5.0.0"
#define MyAppPublisher   "Hồ Văn Thuyên - Trường THPT Hoàng Hoa Thám"
#define MyAppURL         "https://www.giaoducso.org"
#define MyAppDescription "Viết bảng điện tử & chú thích màn hình"
#define MyAppExeName     "OmniPen.exe"

#define PublishX64 "..\publish\win-x64"
#define PublishX86 "..\publish\win-x86"

[Setup]
; AppId nhận diện ứng dụng khi nâng cấp/gỡ – KHÔNG đổi giữa các phiên bản.
AppId={{8B3F6E2A-4C1D-4E7B-9A55-0D7C2F1B9E31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppCopyright=© 2026 {#MyAppPublisher}
AppComments={#MyAppDescription}
VersionInfoVersion={#MyAppFullVersion}
VersionInfoProductVersion={#MyAppFullVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} {#MyAppVersion} Setup
VersionInfoProductName={#MyAppName}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Mặc định cài cho người dùng hiện tại (KHÔNG cần quyền Admin – hợp với máy phòng học bị khóa quyền).
; Người có quyền Admin có thể chọn "cài cho mọi người dùng" ở hộp thoại đầu tiên.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; x64compatible: máy 64-bit (kể cả Windows ARM64 chạy giả lập x64) cài bản 64-bit.
ArchitecturesInstallIn64BitMode=x64compatible
#ifdef X64Only
; build.ps1 -X64Only: chỉ có bản 64-bit → chặn cài trên Windows 32-bit.
ArchitecturesAllowed=x64compatible
#endif
MinVersion=10.0

; Đóng OmniPen đang chạy trước khi ghi đè (nâng cấp).
CloseApplications=yes
; Đăng ký mở file .omnipen bằng OmniPen.
ChangesAssociations=yes
RestartApplications=no

OutputDir=..\dist
OutputBaseFilename=OmniPen_Setup_v{#MyAppVersion}
SetupIconFile=..\src\OmniPen\Assets\OmniPen.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no

[Languages]
; Bản dịch tiếng Việt (không chính thức) dành cho Inno Setup 6.5 trở lên.
#if Ver >= EncodeVer(6,5,0)
Name: "vietnamese"; MessagesFile: "Vietnamese.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
#if Ver >= EncodeVer(6,5,0)
vietnamese.TaskDesktop=Tạo biểu tượng ngoài màn hình Desktop
vietnamese.TaskStartup=Khởi động cùng Windows (chạy ẩn dưới khay hệ thống)
vietnamese.GroupOther=Tùy chọn khác:
vietnamese.LaunchApp=Chạy OmniPen ngay
vietnamese.UninstallShortcut=Gỡ cài đặt OmniPen
#endif
english.TaskDesktop=Create a desktop shortcut
english.TaskStartup=Start with Windows (run in system tray)
english.GroupOther=Other options:
english.LaunchApp=Launch OmniPen now
english.UninstallShortcut=Uninstall OmniPen

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup";     Description: "{cm:TaskStartup}"; GroupDescription: "{cm:GroupOther}"; Flags: unchecked

[Files]
Source: "{#PublishX64}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion; Check: Is64BitInstallMode
#ifndef X64Only
Source: "{#PublishX86}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion; Check: not Is64BitInstallMode
#endif
Source: "..\README.md"; DestDir: "{app}"; DestName: "HuongDan.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppDescription}"
Name: "{autoprograms}\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppDescription}"; Tasks: desktopicon

[Registry]
; Cùng khóa mà app dùng trong Cài đặt → bật/tắt ở đâu cũng đồng bộ.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "OmniPen"; \
    ValueData: """{app}\{#MyAppExeName}"" --tray"; Tasks: startup; Flags: uninsdeletevalue

; Bấm đúp file .omnipen để mở bằng OmniPen (HKA = HKCU khi cài cho 1 người, HKLM khi cài cho mọi người).
Root: HKA; Subkey: "Software\Classes\.omnipen"; ValueType: string; ValueName: ""; ValueData: "OmniPen.Document"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\OmniPen.Document"; ValueType: string; ValueName: ""; ValueData: "Tài liệu OmniPen"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\OmniPen.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\OmniPen.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Tắt OmniPen nếu đang chạy để gỡ được file exe.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillOmniPen"

[Code]
// Xóa mục khởi động cùng Windows kể cả khi người dùng bật nó trong Cài đặt của app (không qua bộ cài).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'OmniPen');
end;
