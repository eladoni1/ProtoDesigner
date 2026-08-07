// Module for the protovalidate harness. Checked in so the runtime check survives deleting the
// gitignored protobuf/ staging directory — the dependencies themselves live in Go's global module
// cache, not in this repo.
module protodesigner/protovalidate-harness

go 1.26.5

require (
	buf.build/go/protovalidate v1.2.0
	google.golang.org/protobuf v1.36.11
)

require (
	buf.build/gen/go/bufbuild/protovalidate/protocolbuffers/go v1.36.11-20260415201107-50325440f8f2.1 // indirect
	cel.dev/expr v0.25.1 // indirect
	github.com/antlr4-go/antlr/v4 v4.13.1 // indirect
	github.com/google/cel-go v0.28.0 // indirect
	golang.org/x/exp v0.0.0-20250813145105-42675adae3e6 // indirect
	golang.org/x/text v0.28.0 // indirect
	google.golang.org/genproto/googleapis/api v0.0.0-20250811230008-5f3141c8851a // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20250811230008-5f3141c8851a // indirect
)
