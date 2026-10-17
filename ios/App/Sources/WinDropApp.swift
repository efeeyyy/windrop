import SwiftUI

@MainActor
@main
struct WinDropApp: App {
    @State private var model = AppModel()
    #if PUSH_ENABLED
    @UIApplicationDelegateAdaptor(PushAppDelegate.self) private var appDelegate
    #endif

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
        }
    }
}
