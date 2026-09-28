package com.picoe.eto.rider

import org.jetbrains.plugins.textmate.api.TextMateBundleProvider

/** Highlighting for .xeto and .jeto files, from the VS Code extension's grammars. */
class EtoTextMateBundleProvider : TextMateBundleProvider {
    override fun getBundles(): List<TextMateBundleProvider.PluginBundle> =
        listOfNotNull(EtoFiles.bundled("textmate")?.let { TextMateBundleProvider.PluginBundle("Eto.Forms", it) })
}
