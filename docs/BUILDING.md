# ��Դ�빹�� InfinityView

## ����

- Windows 11 x64������ʱʹ��ϵͳ .NET Framework 4.8 ����߰汾��
- ��װ `global.json` ָ���� .NET SDK����ǰ10.0.402������ͬ���Դ����²�������
- �Ƽ� PowerShell 7�����������ļ��̶���NuGetԴΪ����nuget.org��

## �й�Ӧ��

�ڲֿ��Ŀ¼���У�

```powershell
pwsh -File scripts/Build-Source.ps1
& ./src/v0.4/LightView.App/bin/InfinityView/Release/net48/InfinityView.exe
```

�ȼ��ֶ����

```powershell
dotnet restore src/v0.4/LightView.App/LightView.App.csproj --locked-mode --configfile NuGet.config
dotnet build src/v0.4/LightView.App/LightView.App.csproj -c Release --no-restore
```

�ֿ��������ԭ��x64 DLL���Լ���ӦԴ�������֤��

## �ؽ�ԭ���⣨��ѡ��

ʹ��x86_64 MinGW-w64 GCC����gcc��objdump����Ŀ¼����PATH��

```powershell
pwsh -File native/lcms2/build.ps1
pwsh -File native/libwebp/build.ps1
```

��Դ��汾����Ŀ¼ `source.json`���������ɼ� `LICENSE` / `COPYING` / `PATENTS`�������ִֻ�ڱ������ɡ�

## �������

```powershell
pwsh -File scripts/Build-Source.ps1 -IncludeTests
& ./tests/v0.4/bin/Release/net48/LightView.Tests.exe --infinity-v1 ./work/source-check "InfinityView data paths"
```

����ͨ����ִ�й������У�ʹ����ں� filter ѡ������������ɫ�ʲ�����׼���ⲿICC�оߡ��ļ�����������ʹ�ø���Ŀ¼���˹�������

## ��Я����

��Ӧ�����Ŀ¼���帴�Ƶ�һ����д����Ŀ¼��������Ϊ `portable` �Ŀ��ļ�������ʹ������ `UserData`����Ҫֻ����EXE��Ҳ��Ҫ�ѱ�Я��Ǽӵ��ճ���װĿ¼���ַ�ʱ������MIT���ɡ�`LICENSE-BRANDING.md`���Լ�ʵ����������������֤�͵����������������ļ���λ�ü� `THIRD_PARTY_NOTICES.md`�����밴MIT��Դ��ͼ�굥������Ȩ�����ַ��޸İ�ʱ�軻��������Ȩʹ�õ�ͼ�������ȡ�����ɡ�

## ��װ��

`installer/InfinityView-v1.iss` ʹ��Inno Setup6.7.3������ʱ���ṩ�����غɡ��汾����װ���ݡ������嵥�����������롣

## ����Ŀ¼

��װ��ʹ�� `%LOCALAPPDATA%/InfinityView`����Я��ʹ������ `UserData`������ `INFINITYVIEW_DATA_DIR` ָ����������Ŀ¼��
