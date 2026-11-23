import Darwin
import Foundation

/// ONNX Runtime for voice cloning, loaded at run time from Microsoft's `libonnxruntime.dylib` (the NuGet package
/// Microsoft.ML.OnnxRuntime, the same version as the Windows version; `scripts/fetch-onnxruntime.sh` gets it and
/// `scripts/build-app.sh` ships it in `Contents/Frameworks`). Microsoft publishes it for Apple silicon only, so on Intel
/// Macs `OnnxRuntime.shared` is nil and voice cloning is unavailable.
///
/// The C API is used directly: `OrtGetApiBase()->GetApi(version)` returns a table of function pointers whose layout is
/// stable (entries are only ever appended), so each function is called by its index in `struct OrtApi` of
/// `onnxruntime_c_api.h`. No header or Swift package is needed to build.
final class OnnxRuntime {
    /// The loaded runtime; nil on Intel Macs and when the library isn't found.
    static let shared: OnnxRuntime? = load()

    /// The API version requested; every function below exists since it.
    static let apiVersion: UInt32 = 16

    /// Indices in `struct OrtApi` (onnxruntime_c_api.h).
    enum Fn: Int {
        case getErrorMessage = 2, createEnv = 3, createSession = 7, run = 9, createSessionOptions = 10
        case setSessionGraphOptimizationLevel = 23, sessionGetOutputCount = 31, sessionGetOutputName = 37
        case createTensorAsOrtValue = 48, getTensorMutableData = 51, getDimensionsCount = 61, getDimensions = 62
        case getTensorShapeElementCount = 64, getTensorTypeAndShape = 65, allocatorFree = 76
        case getAllocatorWithDefaultOptions = 78, releaseEnv = 92, releaseStatus = 93, releaseSession = 95
        case releaseValue = 96, releaseTensorTypeAndShapeInfo = 99, releaseSessionOptions = 100
    }

    /// `ONNXTensorElementDataType`.
    enum ElementType: Int32 { case float = 1, int64 = 7 }

    let version: String
    private let api: UnsafePointer<UnsafeRawPointer?>
    private let env: OpaquePointer
    let allocator: OpaquePointer

    /// Where the library is looked for: `ECC_ONNXRUNTIME` (a path), the app bundle's `Frameworks` (both executables
    /// sit in `Contents/MacOS`), and `build/onnxruntime` of a source checkout above the executable.
    static func candidates() -> [URL] {
        var urls: [URL] = []
        if let path = ProcessInfo.processInfo.environment["ECC_ONNXRUNTIME"] { urls.append(URL(fileURLWithPath: path)) }
        guard let exe = Bundle.main.executableURL?.resolvingSymlinksInPath() else { return urls }
        let dir = exe.deletingLastPathComponent()
        urls.append(dir.appendingPathComponent("../Frameworks/libonnxruntime.dylib").standardizedFileURL)
        var folder = dir
        for _ in 0..<8 {
            urls.append(folder.appendingPathComponent("build/onnxruntime/libonnxruntime.dylib"))
            folder = folder.deletingLastPathComponent()
        }
        return urls
    }

    private static func load() -> OnnxRuntime? {
        #if arch(arm64)
        for url in candidates() where FileManager.default.fileExists(atPath: url.path) {
            if let runtime = try? OnnxRuntime(library: url) { return runtime }
        }
        #endif
        return nil
    }

    private init(library: URL) throws {
        guard let handle = dlopen(library.path, RTLD_NOW | RTLD_LOCAL),
              let symbol = dlsym(handle, "OrtGetApiBase") else {
            throw VoiceClonerError.runFailed("ONNX Runtime didn't load")
        }
        // struct OrtApiBase { const OrtApi* (*GetApi)(uint32_t); const char* (*GetVersionString)(void); }
        typealias GetApiBase = @convention(c) () -> UnsafePointer<UnsafeRawPointer?>?
        typealias GetApi = @convention(c) (UInt32) -> UnsafePointer<UnsafeRawPointer?>?
        typealias GetVersion = @convention(c) () -> UnsafePointer<CChar>?
        guard let base = unsafeBitCast(symbol, to: GetApiBase.self)(),
              let getApi = base[0], let getVersion = base[1],
              let api = unsafeBitCast(getApi, to: GetApi.self)(Self.apiVersion) else {
            throw VoiceClonerError.runFailed("ONNX Runtime is too old")
        }
        self.api = api
        version = unsafeBitCast(getVersion, to: GetVersion.self)().map { String(cString: $0) } ?? "?"

        typealias CreateEnv = @convention(c) (Int32, UnsafePointer<CChar>, UnsafeMutablePointer<OpaquePointer?>) -> OpaquePointer?
        var env: OpaquePointer?
        // ORT_LOGGING_LEVEL_ERROR = 3.
        try Self.check(api, Self.function(api, .createEnv, CreateEnv.self)(3, "EnhanceCapCut", &env))
        typealias GetAllocator = @convention(c) (UnsafeMutablePointer<OpaquePointer?>) -> OpaquePointer?
        var allocator: OpaquePointer?
        try Self.check(api, Self.function(api, .getAllocatorWithDefaultOptions, GetAllocator.self)(&allocator))
        guard let env, let allocator else { throw VoiceClonerError.runFailed("ONNX Runtime didn't start") }
        self.env = env
        self.allocator = allocator
    }

    private static func function<T>(_ api: UnsafePointer<UnsafeRawPointer?>, _ fn: Fn, _: T.Type) -> T {
        unsafeBitCast(api[fn.rawValue]!, to: T.self)
    }

    func function<T>(_ fn: Fn, _ type: T.Type) -> T { Self.function(api, fn, type) }

    /// Throws the status's message (and frees it) when a call failed.
    private static func check(_ api: UnsafePointer<UnsafeRawPointer?>, _ status: OpaquePointer?) throws {
        guard let status else { return }
        typealias Message = @convention(c) (OpaquePointer) -> UnsafePointer<CChar>
        typealias Release = @convention(c) (OpaquePointer) -> Void
        let message = String(cString: function(api, .getErrorMessage, Message.self)(status))
        function(api, .releaseStatus, Release.self)(status)
        throw VoiceClonerError.runFailed(message)
    }

    func check(_ status: OpaquePointer?) throws { try Self.check(api, status) }

    func release(_ fn: Fn, _ object: OpaquePointer) {
        typealias Release = @convention(c) (OpaquePointer) -> Void
        function(fn, Release.self)(object)
    }

    /// A session for the model at `path`, with every graph optimization on.
    func session(path: String) throws -> OnnxSession {
        typealias CreateOptions = @convention(c) (UnsafeMutablePointer<OpaquePointer?>) -> OpaquePointer?
        typealias SetLevel = @convention(c) (OpaquePointer, Int32) -> OpaquePointer?
        typealias CreateSession = @convention(c) (OpaquePointer, UnsafePointer<CChar>, OpaquePointer,
                                                  UnsafeMutablePointer<OpaquePointer?>) -> OpaquePointer?
        var options: OpaquePointer?
        try check(function(.createSessionOptions, CreateOptions.self)(&options))
        guard let options else { throw VoiceClonerError.runFailed("no session options") }
        defer { release(.releaseSessionOptions, options) }
        // ORT_ENABLE_ALL = 99.
        try check(function(.setSessionGraphOptimizationLevel, SetLevel.self)(options, 99))
        var session: OpaquePointer?
        try check(function(.createSession, CreateSession.self)(env, path, options, &session))
        guard let session else { throw VoiceClonerError.runFailed("no session") }
        return OnnxSession(runtime: self, handle: session)
    }

    deinit { release(.releaseEnv, env) }
}

/// An ONNX Runtime session (`OrtSession`).
final class OnnxSession {
    private let runtime: OnnxRuntime
    private let handle: OpaquePointer

    init(runtime: OnnxRuntime, handle: OpaquePointer) {
        self.runtime = runtime
        self.handle = handle
    }

    deinit { runtime.release(.releaseSession, handle) }

    func outputNames() throws -> [String] {
        typealias Count = @convention(c) (OpaquePointer, UnsafeMutablePointer<Int>) -> OpaquePointer?
        typealias Name = @convention(c) (OpaquePointer, Int, OpaquePointer, UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>) -> OpaquePointer?
        typealias Free = @convention(c) (OpaquePointer, UnsafeMutableRawPointer?) -> OpaquePointer?
        var count = 0
        try runtime.check(runtime.function(.sessionGetOutputCount, Count.self)(handle, &count))
        return try (0..<count).map { i in
            var name: UnsafeMutablePointer<CChar>?
            try runtime.check(runtime.function(.sessionGetOutputName, Name.self)(handle, i, runtime.allocator, &name))
            guard let name else { return "" }
            defer { _ = runtime.function(.allocatorFree, Free.self)(runtime.allocator, name) }
            return String(cString: name)
        }
    }

    /// Runs the session on `inputs` and returns the outputs named `outputs`.
    func run(_ inputs: [String: OnnxTensor], _ outputs: Set<String>) throws -> [String: OnnxTensor] {
        typealias Run = @convention(c) (OpaquePointer, OpaquePointer?, UnsafePointer<UnsafePointer<CChar>?>,
                                        UnsafePointer<OpaquePointer?>, Int, UnsafePointer<UnsafePointer<CChar>?>, Int,
                                        UnsafeMutablePointer<OpaquePointer?>) -> OpaquePointer?
        let inputNames = Array(inputs.keys), outputNames = Array(outputs)
        let cInputs = inputNames.map { strdup($0) }, cOutputs = outputNames.map { strdup($0) }
        defer { (cInputs + cOutputs).forEach { free($0) } }
        let values: [OpaquePointer?] = inputNames.map { inputs[$0]!.handle }
        var results = [OpaquePointer?](repeating: nil, count: outputNames.count)
        let inNames = cInputs.map { UnsafePointer($0) }, outNames = cOutputs.map { UnsafePointer($0) }
        try runtime.check(runtime.function(.run, Run.self)(handle, nil, inNames, values, values.count, outNames,
                                                          outNames.count, &results))
        var out: [String: OnnxTensor] = [:]
        for (name, value) in zip(outputNames, results) {
            if let value { out[name] = OnnxTensor(runtime: runtime, handle: value) }
        }
        return out
    }
}

/// A tensor (`OrtValue`) of floats or 64-bit integers, its memory owned by ONNX Runtime.
final class OnnxTensor {
    private let runtime: OnnxRuntime
    fileprivate let handle: OpaquePointer

    fileprivate init(runtime: OnnxRuntime, handle: OpaquePointer) {
        self.runtime = runtime
        self.handle = handle
    }

    /// A tensor of `shape` holding a copy of `values` (`Float` or `Int64`).
    init<T>(_ runtime: OnnxRuntime, _ values: [T], shape: [Int]) throws {
        typealias Create = @convention(c) (OpaquePointer, UnsafePointer<Int64>, Int, Int32,
                                           UnsafeMutablePointer<OpaquePointer?>) -> OpaquePointer?
        let type: OnnxRuntime.ElementType = T.self == Float.self ? .float : .int64
        let dims = shape.map(Int64.init)
        var value: OpaquePointer?
        try runtime.check(runtime.function(.createTensorAsOrtValue, Create.self)(runtime.allocator, dims, dims.count,
                                                                                  type.rawValue, &value))
        guard let value else { throw VoiceClonerError.runFailed("no tensor") }
        self.runtime = runtime
        handle = value
        if !values.isEmpty {
            let data = try mutableData()
            values.withUnsafeBytes { data.copyMemory(from: $0.baseAddress!, byteCount: $0.count) }
        }
    }

    deinit { runtime.release(.releaseValue, handle) }

    private func mutableData() throws -> UnsafeMutableRawPointer {
        typealias Data = @convention(c) (OpaquePointer, UnsafeMutablePointer<UnsafeMutableRawPointer?>) -> OpaquePointer?
        var pointer: UnsafeMutableRawPointer?
        try runtime.check(runtime.function(.getTensorMutableData, Data.self)(handle, &pointer))
        guard let pointer else { throw VoiceClonerError.runFailed("no tensor data") }
        return pointer
    }

    /// The tensor's shape and number of elements.
    func shape() throws -> (dims: [Int], count: Int) {
        typealias Info = @convention(c) (OpaquePointer, UnsafeMutablePointer<OpaquePointer?>) -> OpaquePointer?
        typealias DimCount = @convention(c) (OpaquePointer, UnsafeMutablePointer<Int>) -> OpaquePointer?
        typealias Dims = @convention(c) (OpaquePointer, UnsafeMutablePointer<Int64>, Int) -> OpaquePointer?
        typealias Count = @convention(c) (OpaquePointer, UnsafeMutablePointer<Int>) -> OpaquePointer?
        var info: OpaquePointer?
        try runtime.check(runtime.function(.getTensorTypeAndShape, Info.self)(handle, &info))
        guard let info else { throw VoiceClonerError.runFailed("no tensor shape") }
        defer { runtime.release(.releaseTensorTypeAndShapeInfo, info) }
        var rank = 0
        try runtime.check(runtime.function(.getDimensionsCount, DimCount.self)(info, &rank))
        var dims = [Int64](repeating: 0, count: rank)
        try runtime.check(runtime.function(.getDimensions, Dims.self)(info, &dims, rank))
        var count = 0
        try runtime.check(runtime.function(.getTensorShapeElementCount, Count.self)(info, &count))
        return (dims.map { Int($0) }, count)
    }

    /// The elements, read as `T` (`Float` or `Int64`, the tensor's own type); only the last `last` of them when given.
    func values<T>(_: T.Type, last: Int? = nil) throws -> [T] {
        let count = try shape().count
        let take = min(count, last ?? count)
        guard take > 0 else { return [] }
        let data = try mutableData().assumingMemoryBound(to: T.self)
        return Array(UnsafeBufferPointer(start: data + (count - take), count: take))
    }
}
