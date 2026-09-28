package com.picoe.eto.rider.preview

import com.intellij.execution.filters.TextConsoleBuilderFactory
import com.intellij.execution.ui.ConsoleView
import com.intellij.execution.ui.ConsoleViewContentType
import com.intellij.openapi.Disposable
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.BaseState
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.SimplePersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.components.StoragePathMacros
import com.intellij.openapi.components.service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.FileEditorManagerListener
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.ui.content.ContentFactory
import com.picoe.eto.rider.EtoFiles

private const val TOOL_WINDOW_ID = "Eto Preview"

/** Hidden until a designer file is opened, so it doesn't show up in projects that don't use Eto. */
class PreviewToolWindowFactory : ToolWindowFactory, DumbAware {
    override suspend fun isApplicableAsync(project: Project) = true

    override fun shouldBeAvailable(project: Project) = false

    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val factory = ContentFactory.getInstance()
        val panel = PreviewPanel(project)
        toolWindow.contentManager.addContent(factory.createContent(panel, "Preview", false).apply {
            isCloseable = false
            setDisposer(panel)
        })

        val console = TextConsoleBuilderFactory.getInstance().createBuilder(project).apply { setViewer(true) }.console
        toolWindow.contentManager.addContent(factory.createContent(console.component, "Log", false).apply {
            isCloseable = false
            setDisposer(console)
        })
        project.service<PreviewLog>().attach(console)
    }
}

/** Makes the Eto Preview tool window available once a designer file is opened. */
class PreviewAvailability(private val project: Project) : FileEditorManagerListener {
    override fun fileOpened(source: FileEditorManager, file: VirtualFile) {
        if (!EtoFiles.isPreviewable(file)) return
        ApplicationManager.getApplication().invokeLater({
            ToolWindowManager.getInstance(project).getToolWindow(TOOL_WINDOW_ID)?.isAvailable = true
        }, project.disposed)
    }
}

/** Opens the preview beside the editor, for the file it was run on. */
class OpenPreviewAction : AnAction(), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible = e.project != null && file != null && EtoFiles.isPreviewable(file)
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)?.takeIf { EtoFiles.isPreviewable(it) } ?: return
        val toolWindow = ToolWindowManager.getInstance(project).getToolWindow(TOOL_WINDOW_ID) ?: return
        toolWindow.isAvailable = true
        toolWindow.show {
            val content = toolWindow.contentManager.contents.firstOrNull { it.component is PreviewPanel } ?: return@show
            toolWindow.contentManager.setSelectedContent(content)
            (content.component as PreviewPanel).setFile(file)
        }
    }
}

/** The platform and theme picked in the preview's drop downs, per project. */
@Service(Service.Level.PROJECT)
@State(name = "EtoPreview", storages = [Storage(StoragePathMacros.WORKSPACE_FILE)])
class PreviewPlatformState : SimplePersistentStateComponent<PreviewPlatformState.Options>(Options()) {
    class Options : BaseState() {
        var platform by string(AUTO)
        var theme by string(DEFAULT_THEME)
    }

    /** A platform id, or [AUTO]. */
    var platform: String
        get() = state.platform ?: AUTO
        set(value) {
            state.platform = value
        }

    /** A theme name from the host, or [DEFAULT_THEME]. */
    var theme: String
        get() = state.theme ?: DEFAULT_THEME
        set(value) {
            state.theme = value
        }
}

/** Output from the preview host, shown in the tool window's Log tab. */
@Service(Service.Level.PROJECT)
class PreviewLog : Disposable {
    private val buffer = StringBuilder()
    private var console: ConsoleView? = null

    fun appendLine(text: String) {
        logger<PreviewLog>().info(text)
        synchronized(this) {
            val console = console
            if (console != null) {
                console.print(text + "\n", ConsoleViewContentType.NORMAL_OUTPUT)
                return
            }
            // kept until the tool window opens, trimmed so a chatty host can't grow it forever
            buffer.append(text).append('\n')
            if (buffer.length > 100_000) buffer.delete(0, buffer.length - 100_000)
        }
    }

    fun attach(console: ConsoleView) {
        synchronized(this) {
            this.console = console
            console.print(buffer.toString(), ConsoleViewContentType.NORMAL_OUTPUT)
            buffer.setLength(0)
        }
        Disposer.register(console) {
            synchronized(this) {
                if (this.console === console) this.console = null
            }
        }
    }

    override fun dispose() {}
}
