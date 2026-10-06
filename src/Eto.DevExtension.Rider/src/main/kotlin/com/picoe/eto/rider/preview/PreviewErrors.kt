// setHighlightersToEditor is what the platform's own LSP client uses for results that arrive after highlighting has run
@file:Suppress("DEPRECATION")

package com.picoe.eto.rider.preview

import com.intellij.codeInsight.daemon.impl.HighlightInfo
import com.intellij.codeInsight.daemon.impl.HighlightInfoType
import com.intellij.codeInsight.daemon.impl.UpdateHighlightersUtil
import com.intellij.openapi.components.Service
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.colors.EditorColorsManager
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile

// keeps our highlights apart from the ones the editor's own highlighting manages
private val GROUP = "Eto.PreviewErrors".hashCode()

data class PreviewError(val message: String, val range: ErrorRange)

/** Underlines the error the preview last found in each designer file. */
@Service(Service.Level.PROJECT)
class PreviewErrors(private val project: Project) {
    private val files = mutableSetOf<VirtualFile>()

    /** Call on the UI thread, with null to clear the file's error. */
    fun set(file: VirtualFile, error: PreviewError?) {
        if (error == null && !files.remove(file)) return
        if (error != null) files += file
        show(file, error)
    }

    fun clear() {
        files.toList().forEach { show(it, null) }
        files.clear()
    }

    private fun show(file: VirtualFile, error: PreviewError?) {
        if (project.isDisposed || !file.isValid) return
        val document = FileDocumentManager.getInstance().getDocument(file) ?: return
        val infos = listOfNotNull(error?.let { createInfo(document, it) })
        UpdateHighlightersUtil.setHighlightersToEditor(
            project, document, 0, document.textLength, infos, EditorColorsManager.getInstance().globalScheme, GROUP)
    }

    private fun createInfo(document: Document, error: PreviewError): HighlightInfo? {
        val range = error.range
        // the text has changed since it was drawn, and the next drawing will put it right
        if (range.startLine >= document.lineCount || range.endLine >= document.lineCount) return null
        fun offset(line: Int, column: Int) =
            document.getLineStartOffset(line) + column.coerceIn(0, document.getLineEndOffset(line) - document.getLineStartOffset(line))
        val start = offset(range.startLine, range.startColumn)
        val end = maxOf(start, offset(range.endLine, range.endColumn))
        return HighlightInfo.newHighlightInfo(HighlightInfoType.ERROR).range(start, end).descriptionAndTooltip(error.message).createUnconditionally()
    }
}
