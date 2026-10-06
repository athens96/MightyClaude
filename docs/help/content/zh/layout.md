---
title: 界面布局
order: 2
section: layout
---
窗口分为左侧的侧边栏和右侧的主区域。在主区域中，可以把窗格叠放为标签页，也可以左右或上下分割排列。

![[overview]]

## 侧边栏 {#sidebar}
侧边栏从上到下依次是：
- {{ui:phone.dashboard.title}}：在一处查看所有工作区中窗格的状态。
- {{ui:sidebar.searchPlaceholder}}：按{{kbd:⌘K}}可直接跳到搜索框。
- {{ui:sidebar.workspacesHeader}}列表：点击工作区即可切换到该工作区。点击旁边的箭头可以展开或收起其窗格列表。
- 底部：主题按钮（深色、浅色）和设置按钮。

展开工作区后，最后一行是{{ui:workspace.addPane}}按钮。右键点击窗格所在的行，可以看到{{ui:menu.rename}}和{{ui:menu.closePane}}。

### 收起侧边栏 {#sidebar-collapse}
想让主区域更宽时，可以收起侧边栏。
1. 点击侧边栏顶部的收起按钮，或按{{kbd:⌃⌘S}}。菜单栏的“显示”菜单中也有{{ui:sidebar.collapse}}。
2. 再次展开时，同样使用该按钮或{{kbd:⌃⌘S}}（{{ui:sidebar.expand}}）。

收起状态在重新打开应用后仍会保留。在收起状态下按{{kbd:⌘K}}，侧边栏会展开并跳到搜索框。

![[sidebar-collapsed]]

## 工作状态 {#dashboard}
点击侧边栏最上方的{{ui:phone.dashboard.title}}，可以按状态汇总查看所有工作区的窗格。
- 顶部的格子显示{{ui:phone.dashboard.stat.running}}、{{ui:phone.dashboard.stat.waiting}}和{{ui:phone.dashboard.stat.done}}的数量。
- {{ui:dashboard.usage.title}}卡片显示账户用量。此限额与使用同一账户的其他应用和会话共享。
- 每个工作区都有{{ui:files.pane.title}}按钮和{{ui:workspace.addPane}}菜单。
- 点击卡片即可转到该窗格。选择工作区或窗格后，会离开工作状态页面。

![[dashboard]]

## 添加窗格 {#add-pane}
窗格可以是 AI 智能体（Claude、Codex、Gemini）、终端、浏览器或文件中的一种。

1. 点击侧边栏中的{{ui:workspace.addPane}}按钮。
2. 选择要创建的窗格：Claude、Codex 或 Gemini 窗格，{{ui:workspace.newTerminal}}，{{ui:browser.newTab}}。其他文件夹可以通过同一菜单中的{{ui:menu.openProject}}打开。
3. 新的 AI 窗格会沿用同一智能体最近使用过的窗格的模型、思考强度、权限和视图。对话则从头开始。

快捷键：{{ui:menu.newClaudePane}}为{{kbd:⌘N}}，{{ui:menu.addTerminalPane}}为{{kbd:⌘T}}，{{ui:menu.showFiles}}为{{kbd:⇧⌘E}}。

> [!note]
> Codex 和 Gemini 窗格带有{{ui:badge.beta}}标记。

### 继续之前的会话 {#resume}
添加 Claude 或 Codex 窗格时，如果此文件夹中有之前的会话，会弹出选择窗口。
1. 要开始新对话，请点击{{ui:resume.choice.startNew}}；要继续之前的对话，请点击{{ui:resume.choice.resume}}按钮。
2. 在{{ui:resume.title}}列表中选择会话。可以用{{ui:resume.search}}查找。
3. 新窗格会继续该会话。

![[resume-choice]]

![[resume-list]]

## 标签页与分割 {#tabs-splits}
- 将标签页拖到另一组的中间，即可合并为标签页（{{ui:layout.drop.merge}}）。
- 放到某组的左、右、上、下边缘，就会向该方向分割。
- 拖动分割线可以调整大小，双击分割线则平分两侧。
- 拖动过程中按{{kbd:Esc}}即可取消。
- 右键点击标签页，可以看到{{ui:menu.rename}}、{{ui:menu.focusPane}}和{{ui:menu.closeTab}}。

布局按工作区分别保存。切换工作区后，各自的布局保持不变。

### 窗格菜单 {#pane-menu}
窗格标题栏中的 … 按钮包含以下项目：
- {{ui:menu.rename}}
- {{ui:menu.focusPane}}：只放大显示此窗格。再次点击即可{{ui:pane.menu.restoreLayout}}。
- {{ui:pane.menu.copyLog}}
- {{ui:pane.menu.newConversation}}：从下一个请求起不再接续之前的对话。
- {{ui:menu.closePane}}

## 快捷键 {#shortcuts}
| 快捷键 | 操作 |
|---|---|
| {{kbd:⌘O}} | {{ui:menu.openProject}} |
| {{kbd:⌘N}} | {{ui:menu.newClaudePane}} |
| {{kbd:⌘T}} | {{ui:menu.addTerminalPane}} |
| {{kbd:⇧⌘E}} | {{ui:menu.showFiles}} |
| {{kbd:⌘K}} | {{ui:menu.searchWorkspaces}} |
| {{kbd:⌃⌘S}} | {{ui:sidebar.collapse}} / {{ui:sidebar.expand}} |
| {{kbd:⌘,}} | {{ui:menu.settings}} |
| {{kbd:Enter}} | 发送请求 |
| {{kbd:⇧Enter}} | 换行 |
| {{kbd:⌘Enter}} | 立即发送给正在运行的 Claude |
| {{kbd:Esc}} | 关闭弹出面板、关闭斜杠命令列表 |
