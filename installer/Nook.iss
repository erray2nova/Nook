#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\artifacts\installer\publish"
#endif

#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\artifacts\installer\output"
#endif

#define AppName "Nook"
#define AppVersion "4.1.0"
#define AppPublisher "erray2nova"
#define AppExeName "FastPush.exe"

[Setup]
AppId={{B76E3CA6-7390-4A4E-A8B1-1E28B2CE0CF0}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} Beta 4.1
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/erray2nova/FastPush
AppSupportURL=https://github.com/erray2nova/FastPush/issues
AppUpdatesURL=https://github.com/erray2nova/FastPush/releases
VersionInfoCompany=erray2nova
VersionInfoDescription=Nook customizable desktop launcher
VersionInfoProductName=Nook
VersionInfoProductVersion=4.1.0.0
VersionInfoVersion=4.1.0.0
DefaultDirName={localappdata}\Programs\Nook
DefaultGroupName=Nook
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
SetupIconFile=..\Assets\Nook.ico
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern
WizardSizePercent=100
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
OutputDir={#OutputDir}
OutputBaseFilename=Nook-Setup-Beta-4.1-x64
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
Uninstallable=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.CreateDesktopIcon=Создать ярлык на рабочем столе
russian.AdditionalShortcuts=Дополнительные ярлыки:
russian.LaunchApp=Запустить Nook
english.CreateDesktopIcon=Create a desktop shortcut
english.AdditionalShortcuts=Additional shortcuts:
english.LaunchApp=Launch Nook
russian.WelcomeTitle1=Добро пожаловать в Nook
russian.WelcomeTitle2=Лаунчер, который подстраивается под вас
russian.WelcomeText=Nook объединяет приложения, игры, файлы и папки в удобном настраиваемом пространстве. Перед установкой кратко расскажем о возможностях и требованиях.
russian.FinishedTitle=Nook готов к запуску
russian.FinishedText=Nook установлен. Ваши категории и настройки хранятся в профиле Windows и не удаляются при удалении программы.
russian.AboutPageTitle=Знакомство с Nook
russian.AboutPageDescription=Главное о возможностях, требованиях и хранении данных.
russian.AboutHeadline=Всё нужное — под рукой
russian.AboutIntro=Nook — лёгкий лаунчер для быстрого доступа к вашим приложениям, играм, файлам и папкам.
russian.FeatureTilesTitle=Плитки и папки
russian.FeatureTilesBody=Приложения, файлы, папки и вложенные страницы — в одном месте.
russian.FeatureCustomizeTitle=Ваш стиль
russian.FeatureCustomizeBody=Категории, порядок плиток, иконки, темы и анимированные фоны.
russian.FeatureSearchTitle=Быстрый поиск
russian.FeatureSearchBody=Поиск файлов через локальный индекс Windows Search и доступ к быстрым действиям.
russian.FeaturePrivateTitle=Ваши настройки — ваши
russian.FeaturePrivateBody=Конфигурация хранится локально в профиле Windows; вход в аккаунт не требуется.
russian.SystemRequirements=Требования: Windows 10/11 x64. .NET включён в установщик. Для глобального поиска используется индекс Windows Search.
russian.ProjectLink=GitHub: проект
russian.SupportLink=Сообщить об ошибке
russian.ReleasesLink=Все релизы
english.WelcomeTitle1=Welcome to Nook
english.WelcomeTitle2=A launcher that adapts to you
english.WelcomeText=Nook brings apps, games, files, and folders together in a customizable space. Here is a quick overview of its features and requirements.
english.FinishedTitle=Nook is ready
english.FinishedText=Nook has been installed. Your categories and settings are stored in your Windows profile and are kept if you uninstall the app.
english.AboutPageTitle=Meet Nook
english.AboutPageDescription=An overview of features, requirements, and data storage.
english.AboutHeadline=Everything you need, close at hand
english.AboutIntro=Nook is a lightweight launcher for quick access to your apps, games, files, and folders.
english.FeatureTilesTitle=Tiles and folders
english.FeatureTilesBody=Apps, files, folders, and nested pages in one place.
english.FeatureCustomizeTitle=Make it yours
english.FeatureCustomizeBody=Categories, tile order, icons, themes, and animated backgrounds.
english.FeatureSearchTitle=Find it quickly
english.FeatureSearchBody=Search files through the local Windows Search index and use handy quick actions.
english.FeaturePrivateTitle=Your settings stay yours
english.FeaturePrivateBody=Configuration stays in your local Windows profile; no account is needed.
english.SystemRequirements=Requirements: Windows 10/11 x64. .NET is included. Global search uses the Windows Search index.
english.ProjectLink=Project on GitHub
english.SupportLink=Report an issue
english.ReleasesLink=All releases

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Nook"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall Nook"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Nook"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent

[Messages]
WelcomeLabel1={cm:WelcomeTitle1}
WelcomeLabel2={cm:WelcomeText}
FinishedHeadingLabel={cm:FinishedTitle}
FinishedLabel={cm:FinishedText}

[Code]
var
  AboutPage: TWizardPage;

procedure AddText(Parent: TWinControl; Left, Top, Width, Height, FontSize: Integer;
  Text: String; Bold, Muted: Boolean);
var
  LabelControl: TNewStaticText;
begin
  LabelControl := TNewStaticText.Create(WizardForm);
  LabelControl.Parent := Parent;
  LabelControl.Left := Left;
  LabelControl.Top := Top;
  LabelControl.Width := Width;
  LabelControl.Height := Height;
  LabelControl.AutoSize := False;
  LabelControl.WordWrap := True;
  LabelControl.Caption := Text;
  LabelControl.Font.Name := 'Segoe UI';
  LabelControl.Font.Size := FontSize;
  if Bold then
    LabelControl.Font.Style := [fsBold];
  if Muted then
    LabelControl.Font.Color := $006B7280;
end;

procedure AddFeatureCard(Left, Top, Width: Integer; Title, Description: String);
var
  Card: TPanel;
begin
  Card := TPanel.Create(WizardForm);
  Card.Parent := AboutPage.Surface;
  Card.Left := Left;
  Card.Top := Top;
  Card.Width := Width;
  Card.Height := 72;
  Card.BevelOuter := bvLowered;
  Card.Caption := '';
  AddText(Card, 12, 7, Width - 24, 22, 10, Title, True, False);
  AddText(Card, 12, 30, Width - 24, 36, 9, Description, False, True);
end;

procedure OpenProjectLink(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://github.com/erray2nova/FastPush', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure OpenSupportLink(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://github.com/erray2nova/FastPush/issues', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure OpenReleasesLink(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://github.com/erray2nova/FastPush/releases', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure AddLink(Left, Top, Width: Integer; Caption: String; Handler: TNotifyEvent);
var
  Link: TNewStaticText;
begin
  Link := TNewStaticText.Create(WizardForm);
  Link.Parent := AboutPage.Surface;
  Link.Left := Left;
  Link.Top := Top;
  Link.Width := Width;
  Link.Height := 22;
  Link.AutoSize := False;
  Link.Caption := Caption;
  Link.Font.Name := 'Segoe UI';
  Link.Font.Size := 9;
  Link.Font.Color := clBlue;
  Link.Font.Style := [fsUnderline];
  Link.Cursor := crHand;
  Link.OnClick := Handler;
end;

procedure InitializeWizard;
var
  ColumnGap, ColumnWidth, LinkWidth: Integer;
begin
  AboutPage := CreateCustomPage(wpWelcome, CustomMessage('AboutPageTitle'),
    CustomMessage('AboutPageDescription'));

  AddText(AboutPage.Surface, 0, 0, AboutPage.SurfaceWidth, 30, 18,
    CustomMessage('AboutHeadline'), True, False);
  AddText(AboutPage.Surface, 0, 34, AboutPage.SurfaceWidth, 38, 10,
    CustomMessage('AboutIntro'), False, True);

  ColumnGap := 10;
  ColumnWidth := (AboutPage.SurfaceWidth - ColumnGap) div 2;
  AddFeatureCard(0, 80, ColumnWidth,
    CustomMessage('FeatureTilesTitle'), CustomMessage('FeatureTilesBody'));
  AddFeatureCard(ColumnWidth + ColumnGap, 80, ColumnWidth,
    CustomMessage('FeatureCustomizeTitle'), CustomMessage('FeatureCustomizeBody'));
  AddFeatureCard(0, 160, ColumnWidth,
    CustomMessage('FeatureSearchTitle'), CustomMessage('FeatureSearchBody'));
  AddFeatureCard(ColumnWidth + ColumnGap, 160, ColumnWidth,
    CustomMessage('FeaturePrivateTitle'), CustomMessage('FeaturePrivateBody'));

  AddText(AboutPage.Surface, 0, 240, AboutPage.SurfaceWidth, 30, 8,
    CustomMessage('SystemRequirements'), False, True);
  LinkWidth := (AboutPage.SurfaceWidth - 20) div 3;
  AddLink(0, 276, LinkWidth, CustomMessage('ProjectLink'), @OpenProjectLink);
  AddLink(LinkWidth + 10, 276, LinkWidth, CustomMessage('SupportLink'), @OpenSupportLink);
  AddLink(2 * (LinkWidth + 10), 276, LinkWidth,
    CustomMessage('ReleasesLink'), @OpenReleasesLink);
end;
