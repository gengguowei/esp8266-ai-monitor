import AppKit

// Live "mirror" of the ESP8266 screen, shown in a popover from the menu-bar
// icon. Not a video stream: the Mac re-renders the same scene from the same
// data — /api/info says which app the device is showing (and a sprite_rev
// that bumps when animations change), /sprite/<app>/raw provides the exact
// frames the device draws (custom upload or built-in), and the local
// StatusService supplies the quota numbers the device gets from /status.
// Result: what you see here is what the panel shows, including the walk
// cycle animating only while that app is "working".

// MARK: - RGB565 frame decoding

private func decodeSpriteFrames(_ data: Data, w: Int, h: Int) -> [CGImage] {
    guard data.count >= 1 else { return [] }
    let count = Int(data[data.startIndex])
    let frameBytes = w * h * 2
    guard count > 0, data.count >= 1 + count * frameBytes else { return [] }
    var frames: [CGImage] = []
    let bytes = [UInt8](data)
    for f in 0..<count {
        var rgba = [UInt8](repeating: 255, count: w * h * 4)
        var src = 1 + f * frameBytes
        for p in 0..<(w * h) {
            // wire order is big-endian RGB565 (see tools/convert_sprites.py)
            let v = (UInt16(bytes[src]) << 8) | UInt16(bytes[src + 1])
            src += 2
            rgba[p * 4 + 0] = UInt8((v >> 11) & 0x1F) << 3
            rgba[p * 4 + 1] = UInt8((v >> 5) & 0x3F) << 2
            rgba[p * 4 + 2] = UInt8(v & 0x1F) << 3
        }
        let data = CFDataCreate(nil, rgba, rgba.count)!
        if let provider = CGDataProvider(data: data),
           let img = CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32,
                             bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(),
                             bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                             provider: provider, decode: nil, shouldInterpolate: false,
                             intent: .defaultIntent) {
            frames.append(img)
        }
    }
    return frames
}

// MARK: - the 240x240 replica view

final class MirrorView: NSView {
    // scene state, all in the device's 240x240 logical coordinates
    var frames: [CGImage] = []
    var frameIdx = 0
    var spriteW = 120, spriteH = 120
    var ringPct: Double = 0
    var needsInput = false // shown app waiting on approval -> red border flash
    var flashOn = false
    var line1 = "5h -"
    var line2 = "Weekly -"
    var showingClaude = true
    var deviceOK = false
    var stockMode = false
    var stockRows: [StockMonitor.Row] = []
    private static let claudeLogo = Bundle.module.image(forResource: "claude-logo")
    private static let codexLogo = Bundle.module.image(forResource: "codex-logo")

    override var isFlipped: Bool { true } // draw in the panel's top-left origin

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let scale = bounds.width / 240.0
        ctx.saveGState()
        ctx.scaleBy(x: scale, y: scale)

        // panel background
        let panel = NSBezierPath(roundedRect: NSRect(x: 0, y: 0, width: 240, height: 240),
                                 xRadius: 10, yRadius: 10)
        NSColor.black.setFill()
        panel.fill()
        panel.addClip()

        if stockMode {
            drawStockScene()
            ctx.restoreGState()
            return
        }

        // square quota ring: margin 4, thickness 10, clockwise from top-left
        let m: CGFloat = 4, t: CGFloat = 10
        let side: CGFloat = 240 - 2 * m
        let color = deviceOK ? NSColor(calibratedRed: 0, green: 0.85, blue: 0.2, alpha: 1)
                             : NSColor.darkGray
        color.setFill()
        var remaining = side * 4 * CGFloat(max(0, min(ringPct, 100)) / 100)
        let x0 = m, y0 = m, x1 = 240 - m
        var seg = min(remaining, side)
        if seg > 0 { NSRect(x: x0, y: y0, width: seg, height: t).fill() }          // top
        remaining -= side
        seg = min(remaining, side)
        if seg > 0 { NSRect(x: x1 - t, y: y0, width: t, height: seg).fill() }      // right
        remaining -= side
        seg = min(remaining, side)
        if seg > 0 { NSRect(x: x1 - seg, y: 240 - m - t, width: seg, height: t).fill() } // bottom
        remaining -= side
        seg = min(remaining, side)
        if seg > 0 { NSRect(x: x0, y: 240 - m - seg, width: t, height: seg).fill() }     // left

        // sprite, centered, pixel-crisp
        if !frames.isEmpty {
            let img = frames[min(frameIdx, frames.count - 1)]
            let rect = CGRect(x: 120 - spriteW / 2, y: 120 - spriteH / 2,
                              width: spriteW, height: spriteH)
            ctx.saveGState()
            ctx.interpolationQuality = .none
            // CGContext draws images bottom-up; flip locally around the rect
            ctx.translateBy(x: 0, y: rect.midY)
            ctx.scaleBy(x: 1, y: -1)
            ctx.translateBy(x: 0, y: -rect.midY)
            ctx.draw(img, in: rect)
            ctx.restoreGState()
        }

        // app logo, top-left inside the ring (firmware draws it at 14,18 @40px)
        if let logo = Self.claudeLogo, let logo2 = Self.codexLogo {
            (showingClaude ? logo : logo2).draw(in: NSRect(x: 14, y: 18, width: 40, height: 40))
        }

        // quota text
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedSystemFont(ofSize: 13, weight: .semibold),
            .foregroundColor: NSColor.white,
            .paragraphStyle: style,
        ]
        (line1 as NSString).draw(in: NSRect(x: 0, y: 188, width: 240, height: 18), withAttributes: attrs)
        (line2 as NSString).draw(in: NSRect(x: 0, y: 206, width: 240, height: 18), withAttributes: attrs)

        if !deviceOK {
            let overlay: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 14, weight: .bold),
                .foregroundColor: NSColor.systemRed,
                .paragraphStyle: style,
            ]
            ("设备离线" as NSString).draw(in: NSRect(x: 0, y: 60, width: 240, height: 20),
                                          withAttributes: overlay)
        }

        // approval pending: blink the whole border red over everything else
        if needsInput && flashOn {
            let m: CGFloat = 4, t: CGFloat = 10, side: CGFloat = 240 - 2 * m
            NSColor.systemRed.setFill()
            NSRect(x: m, y: m, width: side, height: t).fill()
            NSRect(x: m, y: 240 - m - t, width: side, height: t).fill()
            NSRect(x: m, y: m, width: t, height: side).fill()
            NSRect(x: 240 - m - t, y: m, width: t, height: side).fill()
        }
        ctx.restoreGState()
    }

    private func drawStockScene() {
        let grey = NSColor(white: 0.55, alpha: 1)
        let codeFont = NSFont.monospacedSystemFont(ofSize: 10, weight: .medium)
        let valueFont = NSFont.monospacedSystemFont(ofSize: 17, weight: .bold)
        if stockRows.isEmpty {
            let style = NSMutableParagraphStyle()
            style.alignment = .center
            ("未配置自选股\n右键菜单 → 设置自选股…" as NSString).draw(
                in: NSRect(x: 0, y: 104, width: 240, height: 40), withAttributes: [
                    .font: NSFont.systemFont(ofSize: 11), .foregroundColor: grey,
                    .paragraphStyle: style,
                ])
            return
        }
        for (i, row) in stockRows.prefix(4).enumerated() {
            let y0 = CGFloat(10 + i * 54)
            let label = row.name.isEmpty ? row.code : "\(row.code)  \(row.name)"
            (label as NSString).draw(at: NSPoint(x: 14, y: y0), withAttributes: [
                .font: codeFont, .foregroundColor: grey,
            ])
            (row.price as NSString).draw(at: NSPoint(x: 14, y: y0 + 15), withAttributes: [
                .font: valueFont, .foregroundColor: NSColor.white,
            ])
            let pctColor = row.up > 0 ? NSColor(calibratedRed: 1, green: 0.23, blue: 0.19, alpha: 1)
                : (row.up < 0 ? NSColor(calibratedRed: 0, green: 0.85, blue: 0.2, alpha: 1)
                              : NSColor.lightGray)
            let style = NSMutableParagraphStyle()
            style.alignment = .right
            (row.pct as NSString).draw(
                in: NSRect(x: 120, y: y0 + 15, width: 106, height: 22), withAttributes: [
                    .font: valueFont, .foregroundColor: pctColor, .paragraphStyle: style,
                ])
        }
        let center = NSMutableParagraphStyle()
        center.alignment = .center
        ("STOCKS" as NSString).draw(
            in: NSRect(x: 0, y: 224, width: 240, height: 12), withAttributes: [
                .font: NSFont.monospacedSystemFont(ofSize: 8, weight: .medium),
                .foregroundColor: grey, .paragraphStyle: center,
            ])
    }

}

// MARK: - popover controller

final class MirrorPopoverController: NSObject, NSPopoverDelegate {
    private let service: StatusService
    private let stockMonitor: StockMonitor
    private let popover = NSPopover()
    private let mirror = MirrorView()
    private let modeControl = NSSegmentedControl(labels: ["自动", "Claude", "Codex", "股票"],
                                                 trackingMode: .selectOne, target: nil, action: nil)
    private let statusLabel = NSTextField(labelWithString: "连接设备中…")
    private let brightnessSlider = NSSlider(value: 100, minValue: 0, maxValue: 100,
                                            target: nil, action: nil)
    private let brightnessValueLabel = NSTextField(labelWithString: "100%")
    // Drag streams many slider events; posts to the single-threaded ESP8266 web
    // server are throttled mid-drag and the final value always flushes on mouse-up.
    private var pendingBrightness: Int?
    private var lastBrightnessSentAt = Date.distantPast

    private var pollTimer: Timer?
    private var animTimer: Timer?
    private var spriteCache: [String: (rev: Int, frames: [CGImage], w: Int, h: Int)] = [:]
    private var lastInfo: DeviceInfo?
    private var fetchingSlot: String?

    init(service: StatusService, stockMonitor: StockMonitor) {
        self.service = service
        self.stockMonitor = stockMonitor
        super.init()
        popover.behavior = .transient
        popover.delegate = self
        popover.contentViewController = makeContent()
    }

    private func makeContent() -> NSViewController {
        let vc = NSViewController()
        let container = NSView(frame: NSRect(x: 0, y: 0, width: 316, height: 424))

        modeControl.target = self
        modeControl.action = #selector(modeChanged)
        statusLabel.font = NSFont.systemFont(ofSize: 11)
        statusLabel.textColor = .secondaryLabelColor
        statusLabel.alignment = .center
        statusLabel.lineBreakMode = .byTruncatingMiddle

        brightnessSlider.target = self
        brightnessSlider.action = #selector(brightnessChanged)
        brightnessSlider.isContinuous = true
        brightnessValueLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .regular)
        brightnessValueLabel.textColor = .secondaryLabelColor
        brightnessValueLabel.alignment = .right
        let brightnessIcon = NSImageView(image: NSImage(systemSymbolName: "sun.max.fill",
                                                        accessibilityDescription: "亮度") ?? NSImage())
        brightnessIcon.contentTintColor = .secondaryLabelColor

        for v in [mirror, modeControl, brightnessIcon, brightnessSlider, brightnessValueLabel, statusLabel] {
            v.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(v)
        }
        NSLayoutConstraint.activate([
            mirror.topAnchor.constraint(equalTo: container.topAnchor, constant: 14),
            mirror.centerXAnchor.constraint(equalTo: container.centerXAnchor),
            mirror.widthAnchor.constraint(equalToConstant: 288),
            mirror.heightAnchor.constraint(equalToConstant: 288),
            modeControl.topAnchor.constraint(equalTo: mirror.bottomAnchor, constant: 12),
            modeControl.centerXAnchor.constraint(equalTo: container.centerXAnchor),
            brightnessIcon.centerYAnchor.constraint(equalTo: brightnessSlider.centerYAnchor),
            brightnessIcon.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 16),
            brightnessSlider.topAnchor.constraint(equalTo: modeControl.bottomAnchor, constant: 10),
            brightnessSlider.leadingAnchor.constraint(equalTo: brightnessIcon.trailingAnchor, constant: 8),
            brightnessSlider.trailingAnchor.constraint(equalTo: brightnessValueLabel.leadingAnchor, constant: -8),
            brightnessValueLabel.centerYAnchor.constraint(equalTo: brightnessSlider.centerYAnchor),
            brightnessValueLabel.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -16),
            brightnessValueLabel.widthAnchor.constraint(equalToConstant: 40),
            statusLabel.topAnchor.constraint(equalTo: brightnessSlider.bottomAnchor, constant: 8),
            statusLabel.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 10),
            statusLabel.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -10),
        ])
        vc.view = container
        return vc
    }

    // MARK: - brightness slider

    @objc private func brightnessChanged() {
        let level = Int(brightnessSlider.doubleValue.rounded())
        brightnessValueLabel.stringValue = "\(level)%"
        let isFinal = NSApp.currentEvent.map { $0.type != .leftMouseDragged } ?? true
        pendingBrightness = level
        if !isFinal && Date().timeIntervalSince(lastBrightnessSentAt) < 0.25 { return }
        flushBrightness()
    }

    private func flushBrightness() {
        guard let level = pendingBrightness else { return }
        pendingBrightness = nil
        lastBrightnessSentAt = Date()
        DeviceClient.setBrightness(level) { _ in }
    }

    func toggle(relativeTo button: NSStatusBarButton) {
        if popover.isShown {
            popover.performClose(nil)
        } else {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            startTimers()
            tick()
        }
    }

    func popoverDidClose(_ notification: Notification) {
        pollTimer?.invalidate()
        animTimer?.invalidate()
        pollTimer = nil
        animTimer = nil
    }

    private func startTimers() {
        pollTimer?.invalidate()
        animTimer?.invalidate()
        pollTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            self?.tick()
        }
        // same cadence as the firmware's ANIM_INTERVAL_MS
        animTimer = Timer.scheduledTimer(withTimeInterval: 0.12, repeats: true) { [weak self] _ in
            self?.animTick()
        }
    }

    private func tick() {
        DeviceClient.fetchInfo { [weak self] result in
            guard let self = self, self.popover.isShown else { return }
            switch result {
            case let .success(info):
                self.lastInfo = info
                self.mirror.deviceOK = true
                self.applyScene(info)
                self.ensureSprite(info)
                self.syncBrightness(info)
                let modeIdx = ["auto": 0, "claude": 1, "codex": 2,
                               "stock": 3][info.mode] ?? 0
                self.modeControl.selectedSegment = modeIdx
                let modeText = info.mode == "auto" ? "自动切换" : "固定显示"
                self.statusLabel.stringValue = "\(info.ip) · \(modeText) · 数据 \(info.bridge)"
            case .failure:
                self.mirror.deviceOK = false
                self.mirror.needsDisplay = true
                self.statusLabel.stringValue = DeviceClient.host.isEmpty
                    ? "未设置设备地址（右键菜单 → 设置设备地址）" : "无法连接 \(DeviceClient.host)"
            }
        }
    }

    /// Follow the device's reported brightness (changed via its web page or
    /// another client) — but never while the user is mid-adjustment here.
    private func syncBrightness(_ info: DeviceInfo) {
        guard pendingBrightness == nil,
              Date().timeIntervalSince(lastBrightnessSentAt) > 2 else { return }
        brightnessSlider.doubleValue = Double(info.brightness)
        brightnessValueLabel.stringValue = "\(info.brightness)%"
    }

    /// Quota lines & ring exactly as the firmware computes them from /status.
    private func applyScene(_ info: DeviceInfo) {
        mirror.stockMode = info.effective == "stock"
        if mirror.stockMode {
            mirror.stockRows = stockMonitor.snapshot
            mirror.needsDisplay = true
            return
        }
        let snap = service.snapshot()
        mirror.showingClaude = info.showing != "codex"
        if mirror.showingClaude {
            let pct = snap.claude.fiveHourPct
                ?? (snap.claude.sessionWindowMin > 0
                    ? 100.0 * Double(snap.claude.sessionMin) / Double(snap.claude.sessionWindowMin) : 0)
            mirror.ringPct = pct
            mirror.line1 = "5h " + Self.pctText(pct)
            mirror.line2 = "Weekly " + Self.pctText(snap.claude.sevenDayPct)
            mirror.needsInput = snap.claude.needsInput
        } else {
            // Codex may only have a weekly window now (5h limit dropped):
            // ring + single line follow whatever windows actually exist.
            mirror.ringPct = snap.codex.primaryPct ?? snap.codex.weeklyPct ?? 0
            if snap.codex.primaryPct == nil, snap.codex.weeklyPct != nil {
                mirror.line1 = "Weekly " + Self.pctText(snap.codex.weeklyPct)
                mirror.line2 = ""
            } else {
                mirror.line1 = "5h " + Self.pctText(snap.codex.primaryPct)
                mirror.line2 = "Weekly " + Self.pctText(snap.codex.weeklyPct)
            }
            mirror.needsInput = snap.codex.needsInput
        }
        mirror.needsDisplay = true
    }

    private static func pctText(_ pct: Double?) -> String {
        guard let p = pct, p >= 0 else { return "-" }
        return "\(Int(p))%"
    }

    private func ensureSprite(_ info: DeviceInfo) {
        let slot = info.showing == "codex" ? "codex" : "claude"
        let w = slot == "claude" ? info.claudeW : info.codexW
        let h = slot == "claude" ? info.claudeH : info.codexH
        if let cached = spriteCache[slot], cached.rev == info.spriteRev {
            mirror.frames = cached.frames
            mirror.spriteW = cached.w
            mirror.spriteH = cached.h
            return
        }
        guard fetchingSlot != slot else { return }
        fetchingSlot = slot
        DeviceClient.fetchSpriteRaw(slot: slot) { [weak self] result in
            guard let self = self else { return }
            self.fetchingSlot = nil
            if case let .success(data) = result {
                let frames = decodeSpriteFrames(data, w: w, h: h)
                guard !frames.isEmpty else { return }
                self.spriteCache[slot] = (info.spriteRev, frames, w, h)
                if (self.lastInfo?.showing == "codex" ? "codex" : "claude") == slot {
                    self.mirror.frames = frames
                    self.mirror.spriteW = w
                    self.mirror.spriteH = h
                    self.mirror.needsDisplay = true
                }
            }
        }
    }

    private var flashCounter = 0

    private func animTick() {
        guard let info = lastInfo, !mirror.stockMode else { return }

        // ~400ms red-border flash while an approval is pending (device cadence)
        if mirror.needsInput {
            flashCounter += 1
            if flashCounter >= 3 { // 3 * 0.12s ≈ 0.36s
                flashCounter = 0
                mirror.flashOn.toggle()
                mirror.needsDisplay = true
            }
        } else if mirror.flashOn {
            mirror.flashOn = false
            mirror.needsDisplay = true
        }

        guard !mirror.frames.isEmpty else { return }
        let snap = service.snapshot()
        let working = info.showing == "codex"
            ? snap.codex.status == "working" : snap.claude.status == "working"
        if working {
            mirror.frameIdx = (mirror.frameIdx + 1) % mirror.frames.count
        } else if mirror.frameIdx != 0 {
            mirror.frameIdx = 0
        }
        mirror.needsDisplay = true
    }

    @objc private func modeChanged() {
        let mode = ["auto", "claude", "codex", "stock"][max(0, modeControl.selectedSegment)]
        DeviceClient.setDisplayMode(mode) { [weak self] _ in self?.tick() }
    }
}
