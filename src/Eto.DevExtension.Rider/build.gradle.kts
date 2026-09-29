import org.jetbrains.intellij.platform.gradle.tasks.PrepareSandboxTask
import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.tasks.KotlinCompile

plugins {
    id("org.jetbrains.kotlin.jvm") version "2.4.20"
    id("org.jetbrains.intellij.platform") version "2.19.0"
}

val repoRoot: File = projectDir.parentFile.parentFile
val srcDir = repoRoot.resolve("src")
val vscodeDir = srcDir.resolve("Eto.DevExtension.VSCode")
val isMac = System.getProperty("os.name").startsWith("Mac", ignoreCase = true)
val isWindows = System.getProperty("os.name").startsWith("Windows", ignoreCase = true)

// keeps the version in step with the other extensions
version = Regex("<DevVersion>(.+?)</DevVersion>").find(repoRoot.resolve("Directory.Build.props").readText())!!.groupValues[1]

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

/** Build number of the Rider install at [dir], or null when it isn't one. */
fun riderBuild(dir: File): String? {
    val info = listOf(dir.resolve("product-info.json"), dir.resolve("Contents/Resources/product-info.json")).firstOrNull { it.isFile } ?: return null
    val text = info.readText()
    if (!Regex("\"productCode\"\\s*:\\s*\"RD\"").containsMatchIn(text)) return null
    return Regex("\"buildNumber\"\\s*:\\s*\"([^\"]+)\"").find(text)?.groupValues?.get(1)
}

/** Rider installs under [dir], looking [depth] folders down, where the first level's name must mention Rider when [named]. */
fun findRiders(dir: File, depth: Int, named: Boolean = true): Sequence<Pair<File, String>> = sequence {
    val build = riderBuild(dir)
    if (build != null) {
        yield(dir to build)
        return@sequence
    }
    if (depth <= 0 || dir.extension == "app") return@sequence
    for (child in dir.listFiles().orEmpty().filter { it.isDirectory }) {
        if (!named || child.name.contains("rider", ignoreCase = true))
            yieldAll(findRiders(child, depth - 1, named = false))
    }
}

/** The newest Rider installed in the usual places for this OS, including JetBrains Toolbox installs. */
fun findRider(): File? {
    val home = File(System.getProperty("user.home"))
    val candidates = when {
        isMac -> listOf(
            findRiders(File("/Applications"), 1),
            findRiders(home.resolve("Applications"), 1),
            // older Toolbox versions
            findRiders(home.resolve("Library/Application Support/JetBrains/Toolbox/apps"), 4),
        )
        isWindows -> {
            val localAppData = System.getenv("LOCALAPPDATA")?.let(::File) ?: home.resolve("AppData/Local")
            val programFiles = System.getenv("ProgramFiles")?.let(::File) ?: File("C:/Program Files")
            listOf(
                // Toolbox
                findRiders(localAppData.resolve("Programs"), 1),
                // standalone installer
                findRiders(programFiles.resolve("JetBrains"), 1),
                // older Toolbox versions
                findRiders(localAppData.resolve("JetBrains/Toolbox/apps"), 3),
            )
        }
        else -> listOf(
            findRiders(home.resolve(".local/share/JetBrains/Toolbox/apps"), 3),
            findRiders(File("/opt"), 2),
            findRiders(File("/opt/JetBrains"), 2),
            findRiders(File("/usr/share"), 1),
            findRiders(File("/usr/local"), 2),
            findRiders(File("/snap/rider/current"), 0),
            findRiders(File("/var/lib/flatpak/app/com.jetbrains.Rider/current/active/files"), 2, named = false),
            findRiders(home.resolve(".local/share/flatpak/app/com.jetbrains.Rider/current/active/files"), 2, named = false),
        )
    }
    val version = Comparator<String> { a, b ->
        val x = a.split('.').map { it.toIntOrNull() ?: 0 }
        val y = b.split('.').map { it.toIntOrNull() ?: 0 }
        (0 until maxOf(x.size, y.size)).map { (x.getOrElse(it) { 0 }).compareTo(y.getOrElse(it) { 0 }) }.firstOrNull { it != 0 } ?: 0
    }
    return candidates.asSequence().flatten().maxWithOrNull(compareBy(version) { it.second })?.first
}

val riderVersion = providers.gradleProperty("riderVersion").get()
val useLocalRider = providers.gradleProperty("useLocalRider").orNull != "false"
val localRider: File? = if (!useLocalRider) null else (providers.gradleProperty("riderPath").orNull ?: System.getenv("RIDER_PATH"))
    ?.takeIf { it.isNotBlank() }?.let(::file)
    ?: findRider()

if (localRider != null)
    logger.lifecycle("Using Rider ${riderBuild(localRider) ?: "(unknown version)"} at $localRider")
else if (useLocalRider)
    logger.lifecycle("No local Rider found, using Rider $riderVersion. Set riderPath to use an installed one.")
else
    logger.lifecycle("Using Rider $riderVersion.")

dependencies {
    intellijPlatform {
        if (localRider != null)
            local(localRider.path)
        else
            rider(riderVersion) { useInstaller = false }
        bundledPlugin("org.jetbrains.plugins.textmate")
    }
}

// Rider 2025.1 runs on Java 21, even when building against a newer Rider
tasks.withType<KotlinCompile>().configureEach {
    compilerOptions.jvmTarget = JvmTarget.JVM_21
}

java {
    sourceCompatibility = JavaVersion.VERSION_21
    targetCompatibility = JavaVersion.VERSION_21
}

intellijPlatform {
    pluginConfiguration {
        version = project.version.toString()
        ideaVersion {
            sinceBuild = "251"
            untilBuild = provider { null }
        }
    }
    // neither is needed, and both slow every build down
    buildSearchableOptions = false
    instrumentCode = false
}

// .NET language server and preview hosts, bundled into the plugin the same way as the VS Code extension

val dotnetDir = layout.buildDirectory.dir("dotnet")
fun binlog(name: String) = "-bl:" + repoRoot.resolve("artifacts/log/rider/$name.binlog").path
val dotnetSources = files(
    repoRoot.resolve("Directory.Build.props"),
    srcDir.resolve("Eto.Designer"),
    srcDir.resolve("Eto.DevExtension.LanguageServer"),
    srcDir.resolve("Eto.DevExtension.PreviewHost"),
    srcDir.resolve("Eto.DevExtension.PreviewHost.macOS"),
).asFileTree.matching { exclude("**/bin/**", "**/obj/**") }

fun dotnetPublish(name: String, project: String, output: String, vararg args: String) = tasks.register<Exec>(name) {
    group = "dotnet"
    inputs.files(dotnetSources).withPropertyName("sources")
    outputs.dir(dotnetDir.map { it.dir(output) })
    doFirst { delete(dotnetDir.map { it.dir(output) }) }
    commandLine("dotnet", "publish", srcDir.resolve("$project/$project.csproj").path, "-c", "Release", "-o", dotnetDir.get().dir(output).asFile.path, *args, binlog(name))
}

val publishLanguageServer = dotnetPublish("publishLanguageServer", "Eto.DevExtension.LanguageServer", "server")
val publishPreviewHostWindows = dotnetPublish("publishPreviewHostWindows", "Eto.DevExtension.PreviewHost", "preview/win", "-f", "net8.0-windows")
val publishPreviewHostNet = dotnetPublish("publishPreviewHostNet", "Eto.DevExtension.PreviewHost", "preview/net", "-f", "net8.0")

// the Eto.macOS host is an app bundle, which only builds on a Mac with the .NET macOS workload
val macAppName = "Eto Preview Host.app"
val macAppBuilt = repoRoot.resolve("artifacts/Eto.DevExtension.PreviewHost.macOS/Release/net10.0-macos/$macAppName")
val buildPreviewHostMac = tasks.register<Exec>("buildPreviewHostMac") {
    group = "dotnet"
    onlyIf { isMac }
    inputs.files(dotnetSources).withPropertyName("sources")
    outputs.dir(macAppBuilt)
    // CI packages must always ship it, but locally a missing workload shouldn't stop the rest
    isIgnoreExitValue = System.getenv("CI") == null
    commandLine("dotnet", "build", srcDir.resolve("Eto.DevExtension.PreviewHost.macOS/Eto.DevExtension.PreviewHost.macOS.csproj").path, "-c", "Release", binlog(name))
    doLast {
        if (executionResult.get().exitValue != 0)
            logger.warn("Could not build the Eto.macOS preview host, so Mac previews will use Eto.Mac64.")
    }
}
val copyPreviewHostMac = tasks.register<Exec>("copyPreviewHostMac") {
    group = "dotnet"
    dependsOn(buildPreviewHostMac)
    onlyIf { isMac && macAppBuilt.exists() }
    inputs.dir(macAppBuilt).optional()
    val target = dotnetDir.map { it.dir("preview/macos/$macAppName") }
    outputs.dir(target)
    doFirst { delete(target) }
    // ditto keeps the bundle's signature intact
    commandLine("ditto", macAppBuilt.path, target.get().asFile.path)
}

// runIde and the tests each have their own sandbox task
tasks.withType<PrepareSandboxTask>().configureEach {
    dependsOn(publishLanguageServer, publishPreviewHostWindows, publishPreviewHostNet, copyPreviewHostMac)
    val pluginDir = intellijPlatform.projectName
    // not "dotnet", which Rider loads into its own backend as ReSharper plugins
    from(dotnetDir) {
        exclude("**/*.pdb")
        into(pluginDir.map { "$it/tools" })
    }
    // the VS Code grammars and language configuration, loaded as a TextMate bundle
    from(vscodeDir) {
        include("package.json", "language-configuration-*.json", "syntaxes/**")
        into(pluginDir.map { "$it/textmate" })
    }
}
