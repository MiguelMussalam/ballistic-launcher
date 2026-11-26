# Ballistic Launcher

Launcher para partidas privadas de **Ballistic Overkill** entre amigos, depois do fim dos servidores oficiais.

Este repositório contém **somente**:
- o código-fonte do launcher (`launcher/BallisticLauncher.cs`, C# 5 / WinForms / .NET Framework 4.x);
- arquivos de configuração do emulador (`dados/steam_settings`): estatísticas, inventário de skins e DLCs;
- a lista de servidores (`dados/servidores.json`).

**Nenhum arquivo do jogo é distribuído aqui.** O launcher pressupõe uma instalação do jogo já existente.

## Atualização automática
Cada versão é um *Release* com `manifest.json` e `manifest.sig`, uma assinatura RSA-SHA256. Ao abrir, o launcher:
1. baixa o manifesto da última release e confere a assinatura com a chave pública embutida no código;
2. compara o SHA-256 de cada arquivo listado com o arquivo local;
3. baixa somente os arquivos diferentes, confere o hash e os substitui. O próprio `Ballistic.exe` se atualiza e reinicia.

Somente caminhos fixos podem ser atualizados (`Ballistic.exe`, `servidores.json` e alguns arquivos de `steam_settings`). A identidade e as configurações de cada jogador nunca são tocadas.

## Compilar
```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /win32icon:launcher\Ballistic.ico ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.Linq.dll ^
  /reference:System.Core.dll /reference:System.Runtime.Serialization.dll /out:Ballistic.exe launcher\BallisticLauncher.cs
```
