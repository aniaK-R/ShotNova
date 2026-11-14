# ShotNova

Capturador de tela leve para Windows 10/11, no estilo do Lightshot: a tela congela, você seleciona a área e edita ali mesmo, sem janela extra.

> Projeto independente, inspirado no fluxo do Lightshot. Não usa código nem recursos do Lightshot.

## Recursos (v0.1.0)

- Captura de área com **atalho global configurável** (padrão: `Print Screen`; ex.: `Ctrl+PrintScreen`).
- Funciona com vários monitores e escalas de DPI diferentes (PerMonitorV2).
- Seleção com alças para mover e redimensionar, com o tamanho em pixels.
- Anotações: caneta, linha, seta, caixa, marca-texto e texto; cores, espessura e desfazer.
- Copiar (`Enter` / `Ctrl+C`), salvar (`Ctrl+S`) ou guardar no histórico (`Ctrl+H`, só quando você escolhe).
- "Salvar" lembra a última pasta e sugere `screenshot_1`, `screenshot_2`, …
- Ícone na bandeja, menu de contexto do Windows (opcional) e início com o Windows (opcional).

Tudo é local: nada é enviado para a internet.

## Instalação

1. Instale o **.NET Desktop Runtime 10** (x64): <https://dotnet.microsoft.com/download/dotnet/10.0>
2. Baixe e execute `ShotNova-Setup-0.1.0.exe` (instalação por usuário, não pede administrador).

No Windows 11, as entradas do menu de contexto aparecem em **Mostrar mais opções**.

## Atalhos durante a captura

| Ação | Atalho |
| --- | --- |
| Copiar e fechar | `Enter` ou `Ctrl+C` |
| Salvar como… | `Ctrl+S` |
| Guardar no histórico | `Ctrl+H` |
| Desfazer anotação | `Ctrl+Z` |
| Cancelar | `Esc` |
| Desfazer a seleção | botão direito |

## Compilar

Requisitos: .NET SDK 10 e, para o instalador, Inno Setup 6.

```powershell
# executar em modo desenvolvimento
dotnet run -c Release

# publicar o .exe e gerar o instalador em installer-out\
powershell -File tools\build-installer.ps1

# regenerar o ícone
powershell -File tools\make-icon.ps1
```

## Configurações

Ficam em `%APPDATA%\ShotNova\settings.json` (atalho, última pasta e contador do `screenshot_N`).

## Planejado

- Gravação de vídeo (tela inteira, uma janela ou uma área).

## Licença

Distribuído sob a **GNU General Public License v3.0**. Veja o arquivo [LICENSE](LICENSE).
