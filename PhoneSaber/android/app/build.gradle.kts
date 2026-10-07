plugins {
    id("com.android.application")
}

// 正式manifestだけをbuild内へ配置し、PNGは既存iOS fixtureを直接参照する。
val fixtureManifestAssets = layout.buildDirectory.dir("generated/androidTest/manifestAssets")
val copyFixtureManifest by tasks.registering(Sync::class) {
    from("../../ios/PhoneSaberSender/Tools/lossless_regression_manifest.json")
    into(fixtureManifestAssets)
}
tasks.matching { it.name == "mergeDebugAndroidTestAssets" }.configureEach {
    dependsOn(copyFixtureManifest)
}

android {
    namespace = "jp.phonesaber.sender"
    compileSdk = 37
    buildToolsVersion = "36.0.0"
    ndkVersion = "30.0.16248370"

    defaultConfig {
        applicationId = "jp.phonesaber.sender"
        minSdk = 29
        targetSdk = 35
        versionCode = 1
        versionName = "1.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk { abiFilters += "arm64-v8a" }
        externalNativeBuild {
            cmake { arguments += "-DANDROID_STL=c++_shared" }
        }
    }
    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }
    buildFeatures { buildConfig = true }
    sourceSets.getByName("androidTest") {
        assets.srcDir("../../ios/PhoneSaberSenderTests/Fixtures")
        // AGP 9 は Provider を SourceSet に渡せないので、同じ場所を File で指定する。
        assets.srcDir(fixtureManifestAssets.get().asFile)
    }
    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
kotlin {
    compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) }
}

dependencies {
    implementation("androidx.activity:activity-ktx:1.13.0")
    implementation("androidx.core:core-ktx:1.17.0")
    implementation("androidx.camera:camera-core:1.6.2")
    implementation("androidx.camera:camera-camera2:1.6.2")
    implementation("androidx.camera:camera-lifecycle:1.6.2")
    implementation("androidx.camera:camera-view:1.6.2")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("junit:junit:4.13.2")
}
