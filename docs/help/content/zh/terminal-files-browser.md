---
title: 终端、文件和浏览器窗格
order: 6
section: terminal-files-browser
---
可以在智能体窗格旁同时打开终端、文件和浏览器窗格。

## 终端窗格 {#terminal}
在工作区文件夹中打开的真实终端。
1. 选择{{ui:menu.addTerminalPane}}（{{kbd:⌘T}}），或选择{{ui:workspace.addPane}} → {{ui:workspace.newTerminal}}。
2. 直接输入命令。

Shell 退出后，请点击{{ui:terminal.restart}}。之前的命令记录可以在窗格菜单的{{ui:pane.menu.terminalHistory}}中查看。退出应用后，终端不会重新打开。

![[terminal-pane]]

### 智能体终端 {#agent-terminal}
当 Claude 或 Codex 运行需要你查看的命令（开发服务器、登录等）时，智能体窗格右侧会出现专用的终端窗格。你可以直接向运行中的命令输入内容。关闭该窗格后，命令仍会继续运行，直到退出应用；可以用窗格标题栏中的{{ui:agentTerminal.terminalPane.open}}按钮重新打开。

## 文件窗格 {#files}
用于浏览工作区文件的只读窗格，不会修改文件。
1. 选择{{ui:menu.showFiles}}（{{kbd:⇧⌘E}}），或点击工作区标题中的文件夹按钮。
2. 在左侧列表中选择文件。用{{ui:files.tree.filter}}查找名称，用方向键和{{kbd:Return}}移动。
3. 在右侧查看内容。

| 文件 | 显示方式 |
|---|---|
| 源代码、文本 | 显示行号和语法高亮。大文件只显示前 1 MB。 |
| Markdown | 在{{ui:files.markdown.rendered}}和{{ui:files.markdown.source}}视图之间切换。 |
| 图片、PDF | {{ui:files.image.fit}}、{{ui:files.image.actualSize}}、{{ui:files.image.zoomIn}}、{{ui:files.image.zoomOut}}。PDF 只显示第一页。 |
| 其他 | {{ui:files.preview.unsupported}}。可以用{{ui:menu.showInFinder}}打开。 |

工作区以外的文件不会显示。如果文件有变化，请点击{{ui:files.tree.refresh}}。

![[files-pane]]

## 浏览器窗格 {#browser}
在应用内打开网页的窗格。这是实验性功能，默认处于关闭状态。
1. 在{{ui:menu.settings}} → {{ui:settings.nav.general}}中打开{{ui:settings.display.browserToggle}}。
2. 重新打开应用。
3. 选择{{ui:workspace.addPane}} → {{ui:browser.newTab}}。
4. 输入网址，并使用{{ui:browser.back}}、{{ui:browser.forward}}、{{ui:browser.reload}}。

登录状态按工作区分别保存。

> [!note]
> 浏览器引擎仅在搭载 Apple 芯片的 Mac 上运行。在不含该引擎的版本中，窗格内会显示说明。

### 智能体打开链接时 {#agent-links}
当智能体想打开网页时，窗格中会弹出{{ui:agentTerminal.urlOpen.dialogTitle}}卡片。
- 选择{{ui:agentTerminal.urlOpen.inAppButton}}或{{ui:agentTerminal.urlOpen.externalButton}}按钮。
- 打开{{ui:agentTerminal.urlOpen.rememberToggle}}后，以后不再询问。
- 如果 30 秒内没有选择，会在应用内打开。如果浏览器窗格已关闭，则在系统浏览器中打开。

此选择可以在{{ui:settings.nav.general}}的{{ui:agentTerminal.urlOpen.settingTitle}}中更改：{{ui:agentTerminal.urlOpen.settingAsk}}、{{ui:agentTerminal.urlOpen.settingInApp}}、{{ui:agentTerminal.urlOpen.settingExternal}}。
