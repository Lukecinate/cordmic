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
        versionCode = 4
        versionName = "0.3.1"
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
            isMinifyEnabled = false
            signingConfig = if (signingProps != null) signingConfigs.getByName("release") else signingConfigs.getByName("debug")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
}
