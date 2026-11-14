# ShotNova

Capturador de tela leve para Windows 10/11, no estilo do Lightshot: a tela congela, você seleciona a área e edita ali mesmo, sem janela extra. Também grava vídeo.

> Projeto independente, inspirado no fluxo do Lightshot. Não usa código nem recursos do Lightshot.

## Recursos

**Capturas**

- Captura de área com **atalho global configurável** (padrão: `Print Screen`; ex.: `Ctrl+PrintScreen`).
- Funciona com vários monitores e escalas de DPI diferentes (PerMonitorV2).
- Seleção com alças para mover e redimensionar, com o tamanho em pixels.
- Anotações: caneta, linha, seta, caixa, marca-texto e texto; cores, espessura e desfazer.
- Copiar (`Enter` / `Ctrl+C`), salvar (`Ctrl+S`) ou guardar no histórico (`Ctrl+H`, só quando você escolhe).
- "Salvar" lembra a última pasta e sugere `screenshot_1`, `screenshot_2`, …

**Vídeo**

- Grava a **tela inteira** (monitor onde está o cursor), uma **janela** (clique para escolher; acompanha a janela se ela for movida) ou uma **área** selecionada.
- MP4 (H.264, 30 fps) com o cursor do mouse, salvo em `Vídeos\ShotNova\video_1.mp4`, `video_2.mp4`, …
- Durante a gravação aparece uma moldura vermelha e uma barra com cronômetro e botão **Parar**; as duas ficam fora do vídeo.
- O mesmo atalho de captura também **para** a gravação.
- **Áudio**: som do computador (ligado por padrão) e microfone (desligado por padrão), mixados na mesma faixa. Escolha em *Áudio da gravação* no menu da bandeja. Usa o dispositivo de som e o microfone padrão do Windows.

**Geral**

- Ícone na bandeja, menu de contexto do Windows (opcional) e início com o Windows (opcional).
- Tudo é local: nada é enviado para a internet.

## Instalação

1. Instale o **.NET Desktop Runtime 10** (x64): <https://dotnet.microsoft.com/download/dotnet/10.0>
2. Baixe e execute `ShotNova-Setup-0.3.0.exe` (instalação por usuário, não pede administrador).
3. Para gravar vídeo é preciso o **ffmpeg**. Na primeira gravação o ShotNova oferece instalar pelo winget (`winget install Gyan.FFmpeg`). Também funciona com o `ffmpeg.exe` ao lado do `ShotNova.exe` ou no `PATH`.

O ffmpeg é usado como programa separado, só para codificar o vídeo; ele não vem dentro do ShotNova.

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
| Gravar vídeo da área selecionada | botão ● da barra |

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

Ficam em `%APPDATA%\ShotNova\settings.json` (atalho, última pasta e contadores de `screenshot_N` e `video_N`).


## Licença

Distribuído sob a **GNU General Public License v3.0**. Veja o arquivo [LICENSE](LICENSE).

## Créditos

Usa a biblioteca [NAudio](https://github.com/naudio/NAudio) (licença MIT) para capturar áudio e o [ffmpeg](https://ffmpeg.org/) como programa externo para codificar o vídeo.
