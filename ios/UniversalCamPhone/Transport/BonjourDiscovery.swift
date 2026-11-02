import Foundation
import Network
import Combine
import UIKit

/// Discovers Windows-side universal-cam instances on the LAN via Bonjour/mDNS.
/// Also advertises the phone so the Windows app can find it (bidirectional discovery).
///
/// Uses Network.framework's NWBrowser (replacing deprecated NetService APIs).
/// Service type: _universalcam._tcp (must match Info.plist NSBonjourServices entry)
final class BonjourDiscovery: ObservableObject {

    @Published var discovered: [NWEndpoint] = []

    private var browser: NWBrowser?
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "com.universalcam.discovery")

    // MARK: - Public

    func start() {
        startBrowsing()
        advertisePhone()
    }

    func stop() {
        browser?.cancel()
        browser = nil
        listener?.cancel()
        listener = nil
        DispatchQueue.main.async {
            self.discovered = []
        }
    }

    // MARK: - Browsing

    private func startBrowsing() {
        let descriptor = NWBrowser.Descriptor.bonjour(type: "_universalcam._tcp", domain: nil)
        let params = NWParameters()
        params.includePeerToPeer = true

        let browser = NWBrowser(for: descriptor, using: params)
        self.browser = browser

        browser.stateUpdateHandler = { state in
            switch state {
            case .ready:
                print("[BonjourDiscovery] Browser ready")
            case .failed(let error):
                print("[BonjourDiscovery] Browser failed: \(error)")
            case .cancelled:
                print("[BonjourDiscovery] Browser cancelled")
            default:
                break
            }
        }

        browser.browseResultsChangedHandler = { [weak self] results, changes in
            guard let self else { return }
            // Filter to only service endpoints from other devices (skip our own advertisement)
            let endpoints: [NWEndpoint] = results.compactMap { result in
                // NWBrowser.Result.endpoint is an NWEndpoint.service(...)
                if case .service(let name, _, _, _) = result.endpoint {
                    // Skip our own advertised service
                    if name == UIDevice.current.name { return nil }
                }
                return result.endpoint
            }
            DispatchQueue.main.async {
                self.discovered = endpoints
            }
        }

        browser.start(queue: queue)
    }

    // MARK: - Advertising

    /// Advertise the phone via NWListener's built-in Bonjour service publishing.
    private func advertisePhone() {
        let tcpOptions = NWProtocolTCP.Options()
        let params = NWParameters(tls: nil, tcp: tcpOptions)

        do {
            let listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: QuicTransport.port)!)
            listener.service = NWListener.Service(
                name: UIDevice.current.name,
                type: "_universalcam._tcp"
            )

            listener.serviceRegistrationUpdateHandler = { change in
                switch change {
                case .add(let endpoint):
                    print("[BonjourDiscovery] Advertising: \(endpoint)")
                case .remove(let endpoint):
                    print("[BonjourDiscovery] Removed advertisement: \(endpoint)")
                @unknown default:
                    break
                }
            }

            listener.stateUpdateHandler = { state in
                if case .failed(let error) = state {
                    print("[BonjourDiscovery] Advertiser listener failed: \(error)")
                }
            }

            // We don't need to accept connections on this listener — it's just for advertising.
            // Actual connections come through QuicTransport.
            listener.newConnectionHandler = { conn in
                conn.cancel()
            }

            listener.start(queue: queue)
            self.listener = listener
        } catch {
            print("[BonjourDiscovery] Failed to create advertiser: \(error)")
        }
    }
}
