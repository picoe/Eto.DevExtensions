package com.picoe.eto.rider

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.project.DumbAware

/** Jumps between a designer file and its code behind, shown only where there's one to jump to. */
class SwitchDesignerCodeAction : AnAction(), DumbAware {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE)
        e.presentation.isEnabledAndVisible = e.project != null && file != null && EtoFiles.counterpart(file) != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val counterpart = e.getData(CommonDataKeys.VIRTUAL_FILE)?.let { EtoFiles.counterpart(it) } ?: return
        FileEditorManager.getInstance(project).openFile(counterpart, true)
    }
}
