import SwiftUI

struct RootView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        @Bindable var model = model
        TabView(selection: $model.selectedTab) {
            HomeView()
                .tabItem { Label("Ana ekran", systemImage: "house") }
                .tag(AppModel.Tab.home)
            TransfersView()
                .tabItem { Label("Aktarımlar", systemImage: "arrow.up.arrow.down") }
                .badge(model.transfers.pendingOfferCount)
                .tag(AppModel.Tab.transfers)
            SettingsView()
                .tabItem { Label("Ayarlar", systemImage: "gearshape") }
                .tag(AppModel.Tab.settings)
        }
        .onAppear { model.didBecomeActive() }   // ilk açılış: sahne zaten etkin, onChange tetiklenmez
        .onChange(of: scenePhase) { _, phase in
            if phase == .active {
                model.didBecomeActive()
            } else if phase == .background {
                model.didResignActive()
            }
        }
        // Gelen teklif: AirDrop tarzı onay sayfası (kabul/ret dışında kapatılamaz).
        .sheet(item: Binding(get: { model.transfers.pendingOffer }, set: { _ in })) { item in
            OfferSheet(item: item)
                .interactiveDismissDisabled()
        }
        // İlk başarılı aktarımdan sonra bir kez gösterilen bilgi kartı (açılışta gösterilmez).
        .sheet(isPresented: Binding(
            get: { model.transfers.firstSuccessCardPending && model.transfers.pendingOffer == nil },
            set: { if !$0 { model.transfers.dismissInfoCard() } })
        ) {
            InfoCardSheet()
        }
    }
}
