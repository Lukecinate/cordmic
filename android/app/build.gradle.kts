import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "dev.cordmic"
    compileSdk = 34
    ndkVersion = "26.1.10909125"

    defaultConfig {
        applicationId = "dev.cordmic"
        minSdk = 29
        targetSdk = 34
        versionCode = 19
        versionName = "0.7.0"
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64") }
        externalNativeBuild { cmake { cppFlags += "-std=c++17" } }
    }

    externalNativeBuild {
        cmake { path = file("src/main/cpp/CMakeLists.txt"); version = "3.22.1" }
    }

    // Release signing: point CORDMIC_SIGNING_PROPERTIES (or -PcordmicSigning=...) at a properties file with
    // storeFile, storePassword, keyAlias, keyPassword. The keystore and passwords are never stored in the repo.
    // Without it, local release builds fall back to the debug key.
    val signingFile = (findProperty("cordmicSigning") as String?) ?: System.getenv("CORDMIC_SIGNING_PROPERTIES")
    val signingProps = signingFile?.let { f -> file(f).takeIf { it.isFile }?.let { Properties().apply { it.inputStream().use(::load) } } }
    signingConfigs {
        if (signingProps != null) create("release") {
            storeFile = file(signingProps.getProperty("storeFile"))
            storePassword = signingProps.getProperty("storePassword")
            keyAlias = signingProps.getProperty("keyAlias")
            keyPassword = signingProps.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (signingProps != null) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { buildConfig = true }
    testOptions {
        unitTests {
            isIncludeAndroidResources = true
            // ./gradlew testDebugUnitTest -PrecordScreenshots --tests dev.cordmic.ScreenshotTest
            // writes the UI renders to docs/screenshots (Robolectric native graphics, no emulator needed).
            all { test ->
                if (project.hasProperty("recordScreenshots")) {
                    test.systemProperty("cordmic.screenshots.dir", rootProject.file("../docs/screenshots").absolutePath)
                }
            }
        }
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    implementation("com.google.android.material:material:1.12.0")
    testImplementation("junit:junit:4.13.2")
    // JVM screenshot tests (Robolectric native graphics, no emulator)
    testImplementation("org.robolectric:robolectric:4.13")
    testImplementation("androidx.test:core:1.6.1")
}

