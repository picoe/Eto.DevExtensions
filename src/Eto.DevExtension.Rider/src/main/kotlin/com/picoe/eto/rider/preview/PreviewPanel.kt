package com.picoe.eto.rider.preview

import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.application.ReadAction
import com.intellij.openapi.components.service
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.colors.EditorColorsManager
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.FileEditorManagerEvent
import com.intellij.openapi.fileEditor.FileEditorManagerListener
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.ComboBox
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.CollectionComboBoxModel
import com.intellij.ui.JBColor
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.dsl.listCellRenderer.textListCellRenderer
import com.intellij.util.Alarm
import com.intellij.util.ui.GraphicsUtil
import com.intellij.util.ui.JBUI
import com.intellij.util.ui.UIUtil
import com.picoe.eto.rider.EtoFiles
import java.awt.BasicStroke
import java.awt.BorderLayout
import java.awt.Cursor
import java.awt.Dimension
import java.awt.FlowLayout
import java.awt.Graphics
import java.awt.Graphics2D
import java.awt.Point
import java.awt.Rectangle
import java.awt.RenderingHints
import java.awt.event.HierarchyEvent
import java.awt.event.MouseAdapter
import java.awt.event.MouseEvent
import java.awt.geom.Ellipse2D
import java.awt.image.BufferedImage
import java.io.ByteArrayInputStream
import java.util.Base64
import javax.imageio.ImageIO
import javax.swing.JPanel
import javax.swing.JTextArea

private const val REFRESH_DELAY_MS = 500

/**
 * The preview in the Eto Preview tool window, which follows the selected designer file and redraws as it is edited.
 */
class PreviewPanel(private val project: Project) : JPanel(BorderLayout()), Disposable {
    private val host = project.service<PreviewHost>()
    private val state = project.service<PreviewPlatformState>()
    private val alarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, this)
    private val platforms = CollectionComboBoxModel<PlatformOption>()
    private val platformBox = ComboBox(platforms)
    private val surface = Surface()
    private val error = JTextArea()
    private var file: VirtualFile? = null
    private var size: Dimension? = null
    private var scale = 1.0
    private var rendering = false
    private var renderPending = false
    private var disposed = false
    // what Auto picked
    private var autoLabel: String? = null

    init {
        background = editorBackground()
        platformBox.toolTipText = "Platform to draw the preview with"
        // wide enough for what Auto picked
        platformBox.prototypeDisplayValue = PlatformOption(AUTO, "Auto (WinForms)")
        platformBox.renderer = textListCellRenderer {
            if (it?.id == AUTO && autoLabel != null && platformBox.item?.id == AUTO) "Auto ($autoLabel)" else it?.label
        }
        val options = listOf(PlatformOption(AUTO, "Auto")) + host.launcher.getPlatforms()
        platforms.replaceAll(options)
        platformBox.item = options.firstOrNull { it.id == state.platform } ?: options[0]
        platformBox.addActionListener {
            val id = platformBox.item?.id ?: AUTO
            if (id != state.platform) {
                state.platform = id
                surface.status = "Drawing preview…"
                render()
            }
        }
        val toolbar = JPanel(FlowLayout(FlowLayout.RIGHT, JBUI.scale(10), JBUI.scale(6))).apply {
            isOpaque = false
            add(platformBox)
        }

        error.apply {
            isEditable = false
            lineWrap = true
            wrapStyleWord = true
            isVisible = false
            background = JBColor.namedColor("Banner.errorBackground", JBColor(0xFFF7F7, 0x3E2A2A))
            border = JBUI.Borders.compound(
                JBUI.Borders.customLineTop(JBColor.namedColor("Banner.errorBorderColor", JBColor(0xF8D1D1, 0x5E3838))),
                JBUI.Borders.empty(6, 10)
            )
        }

        add(toolbar, BorderLayout.NORTH)
        add(JBScrollPane(surface).apply {
            border = JBUI.Borders.empty()
            viewport.background = editorBackground()
        }, BorderLayout.CENTER)
        add(error, BorderLayout.SOUTH)

        EditorFactory.getInstance().eventMulticaster.addDocumentListener(object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) {
                if (FileDocumentManager.getInstance().getFile(event.document) == file) schedule()
            }
        }, this)
        project.messageBus.connect(this).subscribe(FileEditorManagerListener.FILE_EDITOR_MANAGER, object : FileEditorManagerListener {
            override fun selectionChanged(event: FileEditorManagerEvent) {
                val selected = event.newFile ?: return
                if (EtoFiles.isPreviewable(selected) && selected != file) setFile(selected)
            }
        })
        // opened from the tool window rather than the action, so start with what's being edited
        file = FileEditorManager.getInstance(project).selectedFiles.firstOrNull { EtoFiles.isPreviewable(it) }
        host.onRedraw(this) { ApplicationManager.getApplication().invokeLater({ render() }, ModalityState.any()) }

        // moving to a screen with another scale needs a sharper or smaller image
        addPropertyChangeListener("graphicsConfiguration") { updateScale() }
        // nothing is drawn while hidden, so catch up when shown
        addHierarchyListener {
            if (it.changeFlags and HierarchyEvent.SHOWING_CHANGED.toLong() != 0L && isShowing) {
                updateScale()
                render()
            }
        }
    }

    fun setFile(file: VirtualFile) {
        this.file = file
        // a size picked for one file rarely suits the next
        size = null
        render()
    }

    override fun dispose() {
        disposed = true
    }

    private fun updateScale() {
        val newScale = graphicsConfiguration?.defaultTransform?.scaleX ?: 1.0
        if (newScale != scale) {
            scale = newScale
            render()
        }
    }

    private fun schedule() {
        alarm.cancelAllRequests()
        alarm.addRequest({ render() }, REFRESH_DELAY_MS)
    }

    private fun render() {
        val file = file ?: return
        if (disposed || !isShowing) return
        if (rendering) {
            renderPending = true
            return
        }
        rendering = true
        renderPending = false
        val text = ReadAction.compute<String, Throwable> { FileDocumentManager.getInstance().getDocument(file)?.text ?: "" }
        val request = RenderRequest(file.path, text, size?.width, size?.height, scale)
        host.render(request, state.platform).whenComplete { result, e ->
            ApplicationManager.getApplication().invokeLater({
                rendering = false
                if (disposed) return@invokeLater
                // results for a file the user already moved away from would only flicker
                if (file == this.file)
                    show(result ?: RenderResult(errorMessage = "The preview could not be drawn.", errorDetails = e?.toString()))
                if (renderPending) render()
            }, ModalityState.any())
        }
    }

    private fun show(result: RenderResult) {
        autoLabel = result.platform
        platformBox.repaint()
        if (result.errorMessage != null) {
            // keep the last good preview up
            error.text = result.errorMessage
            error.toolTipText = result.errorDetails
            error.isVisible = true
            surface.status = null
        } else {
            error.isVisible = false
            surface.status = null
            val image = result.image?.let { ImageIO.read(ByteArrayInputStream(Base64.getDecoder().decode(it))) }
            surface.show(image, result.width ?: image?.width ?: 0, result.height ?: image?.height ?: 0, size != null)
        }
        revalidate()
        repaint()
    }

    private fun onResize(width: Int, height: Int) {
        size = Dimension(width, height)
        render()
    }

    private fun onReset() {
        size = null
        render()
    }

    /** The drawn form, centered, with its size above it and a handle to resize it by. */
    private inner class Surface : JPanel(null) {
        private val top get() = JBUI.scale(40)
        private val side get() = JBUI.scale(32)
        private val gripSize get() = JBUI.scale(10)
        private var image: BufferedImage? = null
        private var frameWidth = 0
        private var frameHeight = 0
        private var sized = false
        private var hover = false
        private var drag: Pair<Point, Dimension>? = null
        private var sent = 0L

        var status: String? = "Drawing preview…"
            set(value) {
                field = value
                repaint()
            }

        init {
            isOpaque = true
            background = editorBackground()
            val mouse = object : MouseAdapter() {
                override fun mouseMoved(e: MouseEvent) {
                    val over = image != null && frame().apply { grow(gripSize, gripSize) }.contains(e.point)
                    if (over != hover) {
                        hover = over
                        repaint()
                    }
                    cursor = when {
                        grip().contains(e.point) -> Cursor.getPredefinedCursor(Cursor.SE_RESIZE_CURSOR)
                        sized && badge().contains(e.point) -> Cursor.getPredefinedCursor(Cursor.HAND_CURSOR)
                        else -> Cursor.getDefaultCursor()
                    }
                    toolTipText = when {
                        grip().contains(e.point) -> "Drag to resize"
                        sized && badge().contains(e.point) -> "Click to reset to auto size"
                        else -> null
                    }
                }

                override fun mouseExited(e: MouseEvent) {
                    if (hover && drag == null) {
                        hover = false
                        repaint()
                    }
                }

                override fun mousePressed(e: MouseEvent) {
                    if (image != null && grip().contains(e.point))
                        drag = e.point to Dimension(frameWidth, frameHeight)
                }

                override fun mouseDragged(e: MouseEvent) {
                    val (start, startSize) = drag ?: return
                    // centered, so it grows both ways and the edge under the mouse moves twice as far
                    frameWidth = maxOf(1, startSize.width + (e.x - start.x) * 2)
                    frameHeight = maxOf(1, startSize.height + e.y - start.y)
                    revalidate()
                    repaint()
                    val now = System.currentTimeMillis()
                    if (now - sent > 100) {
                        sent = now
                        onResize(frameWidth, frameHeight)
                    }
                }

                override fun mouseReleased(e: MouseEvent) {
                    if (drag == null) return
                    drag = null
                    repaint()
                    onResize(frameWidth, frameHeight)
                }

                override fun mouseClicked(e: MouseEvent) {
                    if (sized && badge().contains(e.point)) onReset()
                }
            }
            addMouseListener(mouse)
            addMouseMotionListener(mouse)
        }

        fun show(image: BufferedImage?, width: Int, height: Int, sized: Boolean) {
            this.image = image
            this.sized = sized
            // while dragging the frame follows the mouse, and an older result mustn't undo that
            if (drag == null) {
                frameWidth = width
                frameHeight = height
            }
            revalidate()
            repaint()
        }

        private fun frame() = Rectangle(maxOf(side, (width - frameWidth) / 2), top, frameWidth, frameHeight)

        private fun grip(): Rectangle = frame().let { Rectangle(it.x + it.width - 1, it.y + it.height - 1, gripSize, gripSize) }

        private fun badge(): Rectangle {
            val frame = frame()
            val metrics = getFontMetrics(badgeFont())
            val w = metrics.stringWidth(sizeText()) + JBUI.scale(12)
            val h = metrics.height + JBUI.scale(2)
            return Rectangle(frame.x + (frame.width - w) / 2, frame.y - JBUI.scale(30), w, h)
        }

        private fun sizeText() = "${frameWidth}x$frameHeight"

        private fun badgeFont() = JBUI.Fonts.smallFont()

        override fun getPreferredSize(): Dimension =
            if (image == null) Dimension(0, 0)
            else Dimension(frameWidth + side * 2 + gripSize, frameHeight + top + side)

        override fun paintComponent(g: Graphics) {
            super.paintComponent(g)
            val g2 = g.create() as Graphics2D
            try {
                g2.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON)
                GraphicsUtil.setupAntialiasing(g2)
                status?.let {
                    g2.color = UIUtil.getContextHelpForeground()
                    g2.drawString(it, side, JBUI.scale(16) + g2.fontMetrics.ascent)
                }
                val image = image ?: return
                val frame = frame()
                g2.setRenderingHint(RenderingHints.KEY_INTERPOLATION, RenderingHints.VALUE_INTERPOLATION_BILINEAR)
                g2.drawImage(image, frame.x, frame.y, frame.width, frame.height, null)

                val focus = JBUI.CurrentTheme.Focus.focusColor()
                if (hover || drag != null) {
                    val outset = JBUI.scale(4)
                    g2.color = focus
                    g2.stroke = BasicStroke(1f, BasicStroke.CAP_BUTT, BasicStroke.JOIN_MITER, 10f, floatArrayOf(3f, 3f), 0f)
                    g2.drawRect(frame.x - outset, frame.y - outset, frame.width + outset * 2 - 1, frame.height + outset * 2 - 1)
                }

                val badge = badge()
                g2.color = JBColor.namedColor("Counter.background", JBColor(0xCCCCCC, 0x555555))
                g2.fillRoundRect(badge.x, badge.y, badge.width, badge.height, JBUI.scale(4), JBUI.scale(4))
                g2.color = JBColor.namedColor("Counter.foreground", JBColor.foreground())
                g2.font = badgeFont()
                val metrics = g2.fontMetrics
                g2.drawString(sizeText(), badge.x + (badge.width - metrics.stringWidth(sizeText())) / 2, badge.y + JBUI.scale(1) + metrics.ascent)

                val grip = grip()
                g2.color = focus
                g2.fill(Ellipse2D.Float(grip.x.toFloat(), grip.y.toFloat(), grip.width.toFloat(), grip.height.toFloat()))
            } finally {
                g2.dispose()
            }
        }
    }
}

private fun editorBackground() = EditorColorsManager.getInstance().globalScheme.defaultBackground
