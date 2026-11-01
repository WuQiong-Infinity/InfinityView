# InfinityView

<img src="docs/assets/InfinityView.png" alt="InfinityView" width="112" />

���� Windows 11 x64 ������ͼƬ�鿴������ǰ�汾 **1.0.0**��

## ����

��ͨ�û�������룬ֱ������ Windows 11 x64 ����

- **[���ذ�װ��](https://github.com/WuQiong-Infinity/InfinityView/releases/download/v1.0.0/InfinityView-1.0.0-win-x64-setup.exe)**��˫����װ����װǰ�ȹرճ��򴰿ڡ�
- **[�����ⰲװ ZIP](https://github.com/WuQiong-Infinity/InfinityView/releases/download/v1.0.0/InfinityView-1.0.0-win-x64-portable.zip)**��������ѹ���������е� `InfinityView.exe`��
- [����ҳ�桢ʹ��˵����У��ֵ](https://github.com/WuQiong-Infinity/InfinityView/releases/tag/v1.0.0)

��ǰΪ**Ԥ���а棨Pre-release��**��GitHub�ġ�Source code��ѹ������Դ�룬��ͨʹ����ѡ����ĳ����������ʱʹ��Windows�Դ�.NET Framework 4.8��������װ.NET10����ʱ��

## ����

- JPG / JPEG / PNG / BMP / WebP������ WebP ��ʾ��֡��
- ָ���������š���Ӧ���ڡ�100% ���ز鿴��ƽ�ƺ�ȫ����
- ͬ����˫ͼ�Աȡ��������㡢���϶��ָ��ߡ���ѡ����������λ�á�
- ÿ������������� RGB ֱ��ͼ�����Զ�����Ŀ��˳���ͼƬ���顣
- Nikon Picture Control �˾����ƶ�ȡ��
- �������ղظ��ơ���Ƭ�ƶ����Ự�ڳ�����ָ���־��Delete �������վ��
- ICC ɫ��ת����Windows WIC / libwebp ���룬Little CMS ת����
- �������ݼ��Զ��塢��ʵ���򿪡�������Я���ݡ�

## ����������

��Ҫ Windows 11 x64��.NET Framework 4.8��ϵͳ������� [global.json](global.json) ָ���� .NET SDK��

```powershell
pwsh -File scripts/Build-Source.ps1
& ./src/v0.4/LightView.App/bin/InfinityView/Release/net48/InfinityView.exe
```

ԭ�� x64 ���� DLL ��Դ�븽������ӦԴ�롢����ű�������֤Ҳ�����ڲֿ��С�ԭ�����ؽ�����������밲װ��˵���� [�����ĵ�](docs/BUILDING.md)��

## Ĭ�ϲ���

| ���� | ��λ |
| --- | --- |
| ǰ����ͼ | �� / ������ǰ�������Ұ���������� |
| ƽ�� / ȫ�� | �Ҽ��϶� / ����м� |
| ָ������100% / ��Ӧ | �����1 / ���2 |
| ���ҽ��� | A / D |
| ͼƬ�Ա� / ���� | C / X |
| ͼƬ���� / �����ֱ��ͼ | Tab / ������ |
| �� / ���� | F / Home |
| ����Դ����������ʾ / ����������Ϣ | R / Ctrl+C |
| �ղظ��� / ��Ƭ�ƶ� / ����վɾ�� | E / Q / Delete |

����Ŀ��Ĭ�����գ����û����á�����/�ƶ������ļ�ԭ�ֽڣ�ͬ���ļ�����ѡ���Դ�����

## ��Ŀ����

- `src/v0.4/`��Ӧ�ó���ͺ����߼���
- `tests/v0.4/`���Դ���ִ�в��Թ��ߣ����� `dotnet test` ������Ŀ��
- `native/`��Little CMS��libwebp ԭ������Դ�롢���ɼ�����ű���
- `installer/`��Inno Setup ��װ�������뷭�����ɡ�
- `docs/`������������ʹ��˵����ͼ�ꡣ

## ����

������ĵ����Ĳ��� [MIT License](LICENSE.txt)��**ͼ��ͱ�־��Ȩ�� WuQiong-Infinity ����**���� [ͼ������](LICENSE-BRANDING.md) �ṩ��������Χ�� [COPYRIGHT.md](COPYRIGHT.md)��������������ɼ� [����������](THIRD_PARTY_NOTICES.md)��

ʹ��˵���� [�û�ָ��](docs/InfinityView-v1.0-ʹ�������.md)��

## ������

�� [CONTRIBUTORS.md](CONTRIBUTORS.md)��
