// Runs protovalidate against a message built from a generated schema.
//
// This is the other half of the protobuf conformance story. protoc proves the emitted
// buf.validate options *parse* and attach to the right extension; it says nothing about whether
// they bind to the field we meant, with the bound we meant. Only a protovalidate runtime can
// answer that, and it answers it by rejecting a message that breaks the rule.
//
// Nothing here is generated from our schema: the descriptor set is loaded at run time and the
// message is built with dynamicpb, so no protoc-gen-go step is involved and the harness works for
// any schema ProtoDesigner emits without being regenerated alongside it.
//
// Usage:  harness <descriptor-set> <message-full-name>   with the payload as JSON on stdin
// Output: a single JSON object on stdout.
//
//	{"valid":true}
//	{"valid":false,"violations":[{"field":"pressure","rule":"uint32.gte","message":"..."}]}
//	{"error":"..."}                                (exit 1)
package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"

	"buf.build/go/protovalidate"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protodesc"
	"google.golang.org/protobuf/reflect/protoreflect"
	"google.golang.org/protobuf/types/descriptorpb"
	"google.golang.org/protobuf/types/dynamicpb"
)

type violation struct {
	// Field is protovalidate's dotted path to what failed — `pressure`, `samples[2]`, `header.id`.
	Field   string `json:"field"`
	Rule    string `json:"rule"`
	Message string `json:"message"`
}

type report struct {
	Valid      bool        `json:"valid"`
	Violations []violation `json:"violations,omitempty"`
}

func main() {
	if err := run(); err != nil {
		_ = json.NewEncoder(os.Stdout).Encode(map[string]string{"error": err.Error()})
		os.Exit(1)
	}
}

func run() error {
	if len(os.Args) != 3 {
		return fmt.Errorf("usage: harness <descriptor-set> <message-full-name>")
	}

	msg, err := newMessage(os.Args[1], os.Args[2])
	if err != nil {
		return err
	}

	payload, err := io.ReadAll(os.Stdin)
	if err != nil {
		return fmt.Errorf("reading payload: %w", err)
	}

	// DiscardUnknown stays off: a field name the schema does not have is a defect in the test case,
	// and silently dropping it would let a case "pass" while validating nothing.
	if err := protojson.Unmarshal(payload, msg); err != nil {
		return fmt.Errorf("payload does not fit %s: %w", os.Args[2], err)
	}

	return validate(msg)
}

// newMessage loads the descriptor set and builds an empty dynamic message of the named type.
func newMessage(descriptorSet, name string) (*dynamicpb.Message, error) {
	raw, err := os.ReadFile(descriptorSet)
	if err != nil {
		return nil, fmt.Errorf("reading descriptor set: %w", err)
	}

	// The default resolver is the global registry, which already knows buf.validate because
	// protovalidate links the generated package. Options therefore arrive resolved rather than as
	// unknown fields — and protovalidate reparses them anyway, so this holds either way.
	set := &descriptorpb.FileDescriptorSet{}
	if err := proto.Unmarshal(raw, set); err != nil {
		return nil, fmt.Errorf("parsing descriptor set: %w", err)
	}

	files, err := protodesc.NewFiles(set)
	if err != nil {
		return nil, fmt.Errorf("linking descriptor set (was it built with --include_imports?): %w", err)
	}

	desc, err := files.FindDescriptorByName(protoreflect.FullName(name))
	if err != nil {
		return nil, fmt.Errorf("no descriptor named %s: %w", name, err)
	}

	md, ok := desc.(protoreflect.MessageDescriptor)
	if !ok {
		return nil, fmt.Errorf("%s is not a message", name)
	}

	return dynamicpb.NewMessage(md), nil
}

// validate runs protovalidate and prints the report. A rule violation is a result, not a harness
// failure, so it exits 0 — the caller decides which outcome the case expected.
func validate(msg *dynamicpb.Message) error {
	validator, err := protovalidate.New()
	if err != nil {
		return fmt.Errorf("building validator: %w", err)
	}

	err = validator.Validate(msg)
	if err == nil {
		return json.NewEncoder(os.Stdout).Encode(report{Valid: true})
	}

	var validationErr *protovalidate.ValidationError
	if !errors.As(err, &validationErr) {
		return fmt.Errorf("validating: %w", err)
	}

	out := report{Valid: false, Violations: make([]violation, 0, len(validationErr.Violations))}
	for _, v := range validationErr.Violations {
		// The path, not the descriptor: a violation on an element of a repeated field carries no field
		// descriptor at all, so reading only that reports an empty name for exactly the cases where
		// knowing which element failed matters most. FieldPathString renders `samples[2]`.
		field := protovalidate.FieldPathString(v.Proto.GetField())
		if field == "" && v.FieldDescriptor != nil {
			field = string(v.FieldDescriptor.Name())
		}
		out.Violations = append(out.Violations, violation{
			Field:   field,
			Rule:    v.Proto.GetRuleId(),
			Message: v.Proto.GetMessage(),
		})
	}

	return json.NewEncoder(os.Stdout).Encode(out)
}
