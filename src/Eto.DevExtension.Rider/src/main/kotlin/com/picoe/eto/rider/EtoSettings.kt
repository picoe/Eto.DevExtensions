// the replacements for the deprecated LSP API are newer than Rider 2025.1, which is still supported
@file:Suppress("DEPRECATION")

package com.picoe.eto.rider

import com.intellij.openapi.components.BaseState
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.SimplePersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.components.service
import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.ui.DialogPanel
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.panel
import com.picoe.eto.rider.lsp.EtoLspServerSupportProvider

@Service(Service.Level.APP)
@State(name = "EtoFormsDesigner", storages = [Storage("eto-forms-designer.xml")])
class EtoSettings : SimplePersistentStateComponent<EtoSettings.Options>(Options()) {
    class Options : BaseState() {
        var dotnetPath by string("")
        var languageServerPath by string("")
        var previewHostPath by string("")
        var etoAssemblyPath by string("")
    }

    var dotnetPath: String
        get() = state.dotnetPath.orEmpty()
        set(value) { state.dotnetPath = value.trim() }
    var languageServerPath: String
        get() = state.languageServerPath.orEmpty()
        set(value) { state.languageServerPath = value.trim() }
    var previewHostPath: String
        get() = state.previewHostPath.orEmpty()
        set(value) { state.previewHostPath = value.trim() }
    var etoAssemblyPath: String
        get() = state.etoAssemblyPath.orEmpty()
        set(value) { state.etoAssemblyPath = value.trim() }

    companion object {
        val instance: EtoSettings get() = service()
    }
}

/** Settings | Tools | Eto.Forms Designer */
class EtoSettingsConfigurable : BoundConfigurable("Eto.Forms Designer") {
    override fun createPanel(): DialogPanel {
        val settings = EtoSettings.instance
        val serverKey = { listOf(settings.dotnetPath, settings.languageServerPath, settings.etoAssemblyPath) }
        var before = serverKey()
        return panel {
            row("dotnet executable:") {
                textFieldWithBrowseButton(FileChooserDescriptorFactory.createSingleFileNoJarsDescriptor())
                    .bindText(settings::dotnetPath).align(AlignX.FILL)
                    .comment("Runs the language server and preview. Uses dotnet from PATH when empty.")
            }
            row("Language server:") {
                textFieldWithBrowseButton(FileChooserDescriptorFactory.createSingleFileNoJarsDescriptor())
                    .bindText(settings::languageServerPath).align(AlignX.FILL)
                    .comment("Path to Eto.DevExtension.LanguageServer.dll. Uses the bundled copy when empty.")
            }
            row("Preview host:") {
                textFieldWithBrowseButton(FileChooserDescriptorFactory.createSingleFileNoJarsDescriptor())
                    .bindText(settings::previewHostPath).align(AlignX.FILL)
                    .comment("Path to Eto.DevExtension.PreviewHost.dll. Uses the bundled copy when empty. Not used for Eto.macOS previews.")
            }
            row("Eto assembly folder:") {
                textFieldWithBrowseButton(FileChooserDescriptorFactory.createSingleFolderDescriptor())
                    .bindText(settings::etoAssemblyPath).align(AlignX.FILL)
                    .comment("Folder containing the Eto.dll to complete against. Detected from the project when empty.")
            }
            onApply {
                // the server pins one Eto version per session, so these need a fresh process
                val after = serverKey()
                if (after != before) {
                    before = after
                    for (project in ProjectManager.getInstance().openProjects)
                        LspServerManager.getInstance(project).stopAndRestartIfNeeded(EtoLspServerSupportProvider::class.java)
                }
            }
        }
    }
}
