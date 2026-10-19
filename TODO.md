# WinPDF — Quality of Life (QoL) & Polish Roadmap (Pre-Step 4)

Checklist de melhorias de usabilidade, ergonomia visual (Fluent 2 / Grid de 4px) e recursos essenciais de leitura e edição do WinPDF.

---

## 📦 Etapa QoL 1: Barra de Status Inferior Nativa (Estilo Paint / Fotos / Notepad)
- [x] **Remoção dos Controles Antigos:**
  - Remover o seletor de páginas da barra de título superior (`MainWindow.xaml`), mantendo apenas o nome do documento limpo e centralizado.
  - Remover a pílula flutuante de zoom do `DocumentView.xaml`.
- [x] **Barra de Status Inferior Nativa (32 DIPs em Mica):**
  - Criar uma Status Bar fixa na base da janela com `Height="32"` integrada ao backdrop Mica (visível quando um documento está aberto e fora do modo Tela Cheia/Slides).
  - **Alinhados à Esquerda:**
    - Seletor de página compacto estilo *"Inline Pill"* (`Página [ X ] de Y`): `<TextBox>` camaleão com `Height="24"`, `MinHeight="24"`, largura adaptativa (`MinWidth="34"`, `MaxWidth="56"`), auto-select ao focar/clicar (ou via `Ctrl+G`), confirmação com `Enter` e cancelamento com `Esc`.
    - Divisor vertical `|`.
    - Tag de formato `PDF` (preparado para futuros formatos).
    - Divisor vertical `|`.
    - Botão sutil unificado `[ ⓘ  [Ícone Tamanho]  X.X MB/KB ]` (os 3 elementos juntos formam um único botão clicável que abre as **Propriedades do Documento** `Alt+Enter`).
  - **Alinhados à Direita:**
    - `[ToggleButton: Exibição Dupla (Duas Páginas on/off)]`
    - Divisor vertical `|`
    - `[ToggleButton: Rolagem Contínua (Contínuo on/off)]`
    - Divisor vertical `|`
    - `[Botão Fit Vertical (Ajustar à Página)]` `.` `[Botão Fit Horizontal (Ajustar à Largura)]` `.` `[ComboBox/DropDown de Zoom (100% ▾)]` `.` `[Botão - Zoom]` `.` `[Slider Horizontal Compacto de Zoom]` `.` `[Botão + Zoom]`
    - Divisor vertical `|`
    - `[Botão Apresentação de Slides (F5)]` `.` `[Botão Tela Cheia (F11)]`

---

## 📦 Etapa QoL 2: Matriz de Modos de Exibição, Preservação de Estado e Splitter Limpo
- [x] **Matriz Funcional de Modos de Exibição (2×2 via Toggles da Barra Inferior):**
  - **Contínuo ON + Duplo OFF:** Rolagem vertical contínua de 1 coluna.
  - **Contínuo OFF + Duplo OFF:** Modo Slide / Página Única (com *swipe* horizontal em telas de toque e botões discretos `<` e `>` nas extremidades esquerda/direita ao passar o mouse).
  - **Contínuo ON + Duplo ON:** Rolagem vertical contínua exibindo pares de páginas lado a lado.
  - **Contínuo OFF + Duplo ON:** Modo Livro paginado exibindo 2 páginas lado a lado por vez.
- [x] **Preservar Posição e Estado do Documento ao Abrir/Fechar Configurações:**
  - Manter `CurrentPageIndex`, `VerticalOffset`, `HorizontalOffset` e `ZoomFactor` intactos ao alternar para a `SettingsView` e voltar.
- [x] **Divisor da Barra Lateral (Splitter) Invisível:**
  - Remover a pílula visual aparente do `SplitterContainer`, mantendo apenas a área transparente de redimensionamento (`SizeWestEast`).

---

## 📦 Pacote QoL 3: Barra Lateral 2.0 (2 Colunas, Proporção Real e Sincronização)
- [x] **Layout de 2 Colunas para Miniaturas:** Expandir a barra lateral esquerda para exibir 2 tiles por linha.
- [x] **Proporção Real da Página nas Miniaturas:** Respeitar o `AspectRatio` real (`WidthPt / HeightPt`) de cada folha sem forçar A4.
- [x] **Sincronização Automática da Barra Lateral:** Acompanhar a página atual com `ScrollIntoView` suave durante a rolagem do documento.
- [x] **Modo Noturno de Leitura na GPU (Inversão Inteligente via Win2D).**
- [x] **Modo Noturno nas Miniaturas da Barra Lateral:** Propagar `IsNightMode` para `Thumbnails` no `DocumentViewModel` e aplicar inversão instantânea RGB bit a bit (`pixel ^ 0x00FFFFFF`) no `ThumbnailPresenter` (preservando o cache sem re-renderização no PDFium).

---

## 📦 Pacote QoL 4: Teclado, Botão Direito, Modo Noturno e Pesquisa
- [x] **Correção de Foco e Controle por Teclas Direcionais (`Up`, `Down`, `Left`, `Right`).**
- [x] **Atalhos Clássicos:** `PageDown`/`PageUp`, `Espaço`/`Shift+Espaço`, `Home`/`End`, `Ctrl+Roda do Mouse`, `Ctrl+0`, `F11`, `F5`, `Ctrl+R` / `Ctrl+Shift+R`.
- [x] **Menu de Contexto (Botão Direito do Mouse na Página).**
- [x] **Botão de Pesquisa no Documento Funcional (Ctrl + F):** Botão de pesquisa abre uma barra no topo da interface, logo abaixo da titlebar e centralizada, com uma barra de busca, botões anterior e próximo e botão para diferenciar maiúscula de minúscula.

---

## 📦 Pacote QoL 5: Persistência em Disco, Reabrir Último Documento, Metadados e Arquivos
- [x] **Persistência de Configurações em Disco (`%LOCALAPPDATA%\WinPDF\settings.json`).**
- [x] **Opção "Reabrir último documento ao iniciar" nas Configurações.**
- [x] **Lembrar Última Página por Documento:** Retoma a leitura na última página visualizada de cada documento ao reabri-lo.
- [x] **Manter Nível de Zoom por Documento:** Preserva e restaura o nível de zoom individual de cada documento ao reabri-lo.
- [x] **Seção "Avançado" nas Configurações:** Seletor de idioma de exibição do aplicativo com "Padrão do Sistema", separador e opções em ordem alfabética ("English", "Português (Brasil)").
- [x] **Propriedades do Documento Reais (`Alt+Enter` e Clique na Barra Inferior via `FPDF_GetMetaText` em Diálogo Nativo Modal estilo Reprodutor Multimídia com obscurecimento de fundo e coluna única).**
- [x] **Ações de Arquivo (`Ctrl+S` Salvar Como via `FPDF_SaveAsCopy` e `Ctrl+Shift+N` Abrir uma Cópia).**
- [x] **Impressão**: abrir a janela de impressão traz a janela nativa do Windows (`PrintManagerInterop` + `PrintDocument` com Preview ao vivo e fallback Win32).

---

## 📦 Seleção e Pesquisa de Texto (PDFium Text APIs + Highlight na GPU)
- [x] **Interop Nativo de Texto e Busca (`PdfiumNative.cs`):**
  - Mapear as APIs de página de texto do PDFium: `FPDFText_LoadPage`, `FPDFText_ClosePage`, `FPDFText_CountChars`, `FPDFText_GetCharIndexAtPos`, `FPDFText_GetText`, `FPDFText_CountRects`, `FPDFText_GetRect` e `FPDFText_GetBoundedText`.
  - Mapear as APIs de busca: `FPDFText_FindStart`, `FPDFText_FindNext`, `FPDFText_FindPrev`, `FPDFText_GetSchResultIndex`, `FPDFText_GetSchCount` e `FPDFText_FindClose`.
- [x] **Motor de Busca Real no Documento (`Ctrl + F` / `F3` / `Shift + F3`):**
  - Executar a varredura de texto de todas as páginas em background na thread dedicada do `PdfiumEngine` sem bloquear a UI Thread.
  - Extrair os retângulos delimitadores (`Rect` em coordenadas PDF convertidos para DIPs via `PageCoordinateTransform`) de cada ocorrência encontrada.
  - Renderizar o destaque (*highlight*) das ocorrências no `PdfPageCanvas` via Win2D (amarelo translúcido para ocorrências na página e laranja/azul de destaque para a ocorrência ativa).
  - Exibir o contador real (`X de Y`) na barra flutuante de pesquisa (`FloatingSearchBar`) e rolar automaticamente a página/viewport até centralizar a ocorrência ativa ao clicar em Anterior/Próximo (`Enter` / `Shift+Enter`).
- [x] **Seleção Interativa de Texto com o Mouse e Cópia (`Ctrl + C`):**
  - Detectar quando o cursor passa sobre caracteres de texto (`FPDFText_GetCharIndexAtPos`) e alternar o cursor do mouse para `IBeam` (quando `ViewModel.ActiveTool == AnnotationTool.Select`).
  - Permitir clicar e arrastar na página (e duplo clique para selecionar palavra inteira) calculando os retângulos selecionados via `FPDFText_CountRects` e `FPDFText_GetRect`.
  - Desenhar a camada de seleção azul translúcida com cantos suaves sobre o texto no `PdfPageCanvas`.
  - Habilitar `Ctrl + C` e a opção **"Copiar"** no menu de contexto do botão direito, extraindo a string UTF-16 selecionada via `FPDFText_GetText` e enviando para o `Clipboard` do Windows.
- [x] **Links Clicáveis Internos e Externos (`FPDFLink_*`):**
  - Mapear `FPDFLink_GetLinkAtPoint`, `FPDFLink_GetDest` e `FPDFLink_GetAction` para permitir clicar em links de sumário/rodapé dentro da página (navegando para a página destino) ou abrir URLs externas no navegador com confirmação.

---

## 📦 Implementação de Abas (Multi-Document Tabbed Shell)
- [ ] **Refatoração do Estado para Múltiplos Documentos (`ShellViewModel.cs`):**
  - Criar uma coleção observável `ObservableCollection<DocumentViewModel> OpenDocuments` e gerenciar a troca de `CurrentDocument` (aba ativa) preservando de forma independente o estado de cada documento (`CurrentPageIndex`, `ZoomFactor`, `ViewMode`, `LastVerticalOffset`, `LastHorizontalOffset`, histórico de busca e barra lateral).
  - Liberar deterministicamente os recursos nativos (`CloseDocumentAsync`, `TileCache` e bitmaps Win2D) ao fechar uma aba individual sem afetar as demais abas abertas.
- [ ] **Barra de Abas Integrada ao Fluent Design (`MainWindow.xaml`):**
  - Adicionar suporte a abas de documentos usando `TabView` nativo do WinUI 3 (ou faixa de abas compacta integrada ao Mica), exibindo o nome do arquivo, indicador de modificação (`•` / `*` quando `HasUnsavedChanges == true`) e botão de fechar (`×`) ou clique com o botão do meio do mouse.
  - Botão `+` na faixa de abas para abrir um novo PDF rapidamente.
- [ ] **Atalhos de Teclado e Fluxo de Janelas de Abas:**
  - `Ctrl + T` / `Ctrl + O`: Abrir novo arquivo em uma nova aba (ou focar na aba existente caso o mesmo caminho de arquivo já esteja aberto).
  - `Ctrl + W` / `Ctrl + F4`: Fechar a aba atual (voltando para o Empty State se for a última aba).
  - `Ctrl + Tab` / `Ctrl + Shift + Tab`: Alternar rapidamente entre as abas abertas.
  - Suporte a abrir múltiplos arquivos selecionados simultaneamente no `FileOpenPicker` ou via *Drag-and-Drop* de vários PDFs na janela.
  - Persistir a lista de abas abertas (e a aba ativa) em `AppSettings` quando a opção *"Reabrir último documento ao iniciar"* estiver ativa.

---

## 📦 Motor de Anotações, Escrita com Caneta/Toque, Undo/Redo e Salvamento Real
- [ ] **Interop Nativo de Anotações do PDFium (`PdfiumNative.cs`):**
  - Mapear as APIs de leitura, criação e modificação de anotações da `pdfium.dll`:
    - `FPDFPage_GetAnnotCount`, `FPDFPage_GetAnnot`, `FPDFPage_CloseAnnot`, `FPDFAnnot_GetSubtype`, `FPDFAnnot_GetRect`, `FPDFAnnot_SetRect`.
    - `FPDFPage_CreateAnnot`, `FPDFPage_RemoveAnnot`, `FPDFAnnot_SetColor`, `FPDFAnnot_GetColor`.
    - `FPDFAnnot_AddInkStroke`, `FPDFAnnot_RemoveInkList` (para traços de caneta/tinta `FPDF_ANNOT_INK`).
    - `FPDFAnnot_AppendAttachmentPoints`, `FPDFAnnot_CountAttachmentPoints`, `FPDFAnnot_GetAttachmentPoints` (para `QuadPoints` de Marca-texto `FPDF_ANNOT_HIGHLIGHT`, Sublinhado `FPDF_ANNOT_UNDERLINE` e Tachado `FPDF_ANNOT_STRIKEOUT`).
    - `FPDFAnnot_SetStringValue`, `FPDFAnnot_GetStringValue` (para conteúdo de Notas Adesivas `FPDF_ANNOT_TEXT`, autor e datas) integrando com `IPdfPageEditCommitter` e `FPDFPage_GenerateContent` (já prontos em `PdfSaveOptions` e `PdfiumEngine.SaveDocument`).
- [ ] **Ferramentas de Marcação de Texto (`AnnotationTool.Highlighter`, `Underline` e `Strikethrough`):**
  - Ao selecionar texto com uma ferramenta de marcação ativa ou clicar na ferramenta com texto já selecionado, converter os retângulos selecionados em `FS_QUADPOINTSF` nas coordenadas nativas do PDF (`PageCoordinateTransform`), gravar a anotação na página via PDFium e invalidar o cache da página (`_tileCache.RemovePage`) para re-renderizar instantaneamente.
  - Adicionar seletor rápido (Flyout/Paleta) de cores de marca-texto (Amarelo, Verde, Azul, Rosa, Laranja) e espessura/opacidade da caneta.
- [ ] **Desenho Livre / Tinta (`AnnotationTool.Pen`) e Borracha (`AnnotationTool.Eraser`):**
  - Capturar eventos de ponteiro (`PointerPressed`, `PointerMoved`, `PointerReleased`) no `PdfPageCanvas` respeitando a configuração `IsTouchDrawingEnabled` (quando desativada, dedos fazem pan/pinch-zoom no `ScrollViewer` enquanto a caneta Stylus ou o mouse desenham; quando ativada, o toque também desenha).
  - Desenhar o traço em tempo real na camada Win2D com latência zero durante o movimento da caneta/mouse e, ao soltar o ponteiro (`PointerReleased`), converter os pontos para coordenadas PDF (`FPDFAnnot_AddInkStroke`) e persistir na página.
  - Implementar a ferramenta **Borracha (`AnnotationTool.Eraser`)** para remover traços de tinta ou anotações clicadas/tocadas (`FPDFPage_RemoveAnnot`).
- [ ] **Notas Adesivas (`AnnotationTool.TextNote`) e Painel Lateral de Anotações (`AnnotationsTabPage.xaml`):**
  - Permitir clicar em qualquer ponto da página com a ferramenta de Nota Adesiva para inserir um ícone de comentário e abrir um `Flyout` de edição de texto.
  - Popular dinamicamente a aba **Anotações** da barra lateral (`DocumentViewModel.AnnotationsSummary`) listando todas as anotações do documento agrupadas por página, com clique para navegar até a página/anotação, edição de comentário e botão de exclusão.
- [ ] **Pilha de Undo / Redo (`Ctrl + Z` / `Ctrl + Y`) e Salvamento Incremental:**
  - Implementar histórico de comandos de anotação por documento (`UndoCommand` / `RedoCommand`) e adicionar os botões Desfazer/Refazer na barra de ferramentas de anotações.
  - Marcar o documento como modificado (`HasUnsavedChanges = true`), habilitar o salvamento direto (`Ctrl + S` -> `SaveAsync`) e avisar o usuário com diálogo de confirmação ("Deseja salvar as alterações?") antes de fechar uma aba ou encerrar o aplicativo com modificações pendentes.

---

## 📦 Formulários (AcroForms), Anexos, Marcadores Persistentes, Updater e Release
- [ ] **Exportação de Anexos Embutidos (`AttachmentsTabPage.xaml`):**
  - A leitura real via `FPDFDoc_GetAttachmentCount`, `FPDFDoc_GetAttachment` e `FPDFAttachment_GetName` já está implementada em `PdfiumEngine.ReadAttachmentNames`.
  - Mapear `FPDFAttachment_GetFile` e implementar o comando de **Salvar/Exportar Anexo** em disco usando `FileSavePicker` ao clicar em um anexo na aba `AttachmentsTabPage`.
- [ ] **Marcadores de Usuário Persistentes (`BookmarksTabPage.xaml`):**
  - Persistir os marcadores criados pelo usuário por documento (associados ao caminho/hash do arquivo em `%LOCALAPPDATA%\WinPDF\bookmarks.json`), permitindo renomear e excluir marcadores entre sessões.
- [ ] **Preenchimento Básico de Formulários Interativos (AcroForms):**
  - Integrar `FPDFDOC_InitFormFillEnvironment` e `FPDFDOC_ExitFormFillEnvironment` no `PdfiumEngine` para permitir interação básica com campos de texto e caixas de seleção (`CheckBox` / `RadioButton`) em PDFs de formulários e salvar os valores preenchidos via `FPDF_SaveAsCopy`.
- [ ] **Associação de Arquivos no Windows e Redirecionamento Single-Instance:**
  - O processamento de argumentos de linha de comando (`Environment.GetCommandLineArgs()` e `args.Arguments` com `IsInitialDocumentOpening`) em `App.xaml.cs` já está implementado.
  - Adicionar redirecionamento de instância única (`AppInstance.FindOrRegisterForKey`) para abrir arquivos clicados no Explorer como novas abas na janela existente e registrar a associação `.pdf` no instalador/manifesto.
- [ ] **Sistema de Atualização Automática via GitHub Releases (`SettingsView.xaml`):**
  - Implementar o `UpdateService` consultando `https://api.github.com/repos/pedrofleck/winpdf/releases` sem necessidade de servidor externo.
  - Adicionar na seção **Sobre** da `SettingsView.xaml` o botão **"Verificar atualizações"** e uma chave seletora (*ToggleSwitch*) **"Incluir versões de teste (Pre-release)"**, exibindo o status e baixando/executando o instalador automaticamente.
- [ ] **Empacotamento, Instalador e Publicação (Fluentify):**
  - Criar script de automação do instalador Inno Setup (`tools/Build-Installer.ps1` + `installer.iss`) para gerar o executável único `WinPDF-Setup.exe` enxuto para distribuição direta e auto-update via GitHub.
  - Configurar o manifesto de empacotamento (`Package.appxmanifest`) para geração do pacote `.msixupload` assinado para submissão na **Microsoft Store** sob o publicador **Fluentify**.
  - Expandir a suíte `AutoTestRunner.cs` (`tools/Run-AutoTest.ps1`) cobrindo seleção/busca de texto, abertura/fechamento de múltiplas abas e ciclo de criação/remoção de anotações com zero vazamento de handles nativos.
