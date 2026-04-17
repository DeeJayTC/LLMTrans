// llmtrans-mcp-bridge-go
//
// Behavior parity with the Node CLI at ../LlmTrans.McpBridge, minus the npm-install
// roundtrip. Useful for environments that can't install Node (Docker scratch images,
// corporate machines, etc.) and for users who prefer a single static binary.
//
// Build:
//     go build -o llmtrans-mcp-bridge ./...
//
// Usage:
//     llmtrans-mcp-bridge --route rt_xxx [--endpoint URL] [--passthrough] [--log PATH] -- <cmd> [args...]
//     llmtrans-mcp-bridge doctor --route rt_xxx [--endpoint URL]
package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"strings"
	"sync"
	"time"
)

const (
	defaultEndpoint = "https://api.llmtrans.example.com"
	defaultTimeout  = 15 * time.Second
	version         = "0.1.0"
)

type args struct {
	mode          string
	route         string
	endpoint      string
	passthrough   bool
	logPath       string
	timeout       time.Duration
	upstreamCmd   string
	upstreamArgs  []string
}

func main() {
	a, err := parseArgs(os.Args[1:])
	if err != nil {
		fmt.Fprintf(os.Stderr, "llmtrans-mcp-bridge: %v\n\n%s\n", err, helpText)
		os.Exit(2)
	}

	switch a.mode {
	case "help":
		fmt.Print(helpText)
	case "version":
		fmt.Println(version)
	case "doctor":
		if err := doctor(a); err != nil {
			os.Exit(1)
		}
	case "bridge":
		code, err := runBridge(context.Background(), a)
		if err != nil {
			fmt.Fprintf(os.Stderr, "llmtrans-mcp-bridge fatal: %v\n", err)
			os.Exit(70)
		}
		os.Exit(code)
	}
}

func parseArgs(argv []string) (*args, error) {
	if len(argv) > 0 {
		switch argv[0] {
		case "--help", "-h", "help":
			return &args{mode: "help"}, nil
		case "--version", "-v":
			return &args{mode: "version"}, nil
		}
	}

	a := &args{
		mode:     "bridge",
		endpoint: defaultEndpoint,
		timeout:  defaultTimeout,
	}
	start := 0
	if len(argv) > 0 && argv[0] == "doctor" {
		a.mode = "doctor"
		start = 1
	}

	sawSeparator := false
	for i := start; i < len(argv); i++ {
		t := argv[i]
		if t == "--" {
			sawSeparator = true
			rest := argv[i+1:]
			if len(rest) > 0 {
				a.upstreamCmd = rest[0]
				a.upstreamArgs = rest[1:]
			}
			break
		}
		next := func(flag string) (string, error) {
			if i+1 >= len(argv) || strings.HasPrefix(argv[i+1], "--") {
				return "", fmt.Errorf("%s expects a value", flag)
			}
			i++
			return argv[i], nil
		}
		switch t {
		case "--route", "-r":
			v, err := next(t)
			if err != nil {
				return nil, err
			}
			a.route = v
		case "--endpoint", "-e":
			v, err := next(t)
			if err != nil {
				return nil, err
			}
			a.endpoint = strings.TrimSuffix(v, "/")
		case "--passthrough":
			a.passthrough = true
		case "--log":
			v, err := next(t)
			if err != nil {
				return nil, err
			}
			a.logPath = v
		case "--timeout":
			v, err := next(t)
			if err != nil {
				return nil, err
			}
			ms, err := parsePositiveInt(v)
			if err != nil || ms < 100 {
				return nil, fmt.Errorf("--timeout must be ≥ 100ms (got %s)", v)
			}
			a.timeout = time.Duration(ms) * time.Millisecond
		default:
			return nil, fmt.Errorf("unknown flag: %s", t)
		}
	}

	if a.route == "" {
		return nil, errors.New("missing --route (your llmtrans route token)")
	}
	if a.mode == "bridge" && (!sawSeparator || a.upstreamCmd == "") {
		return nil, errors.New("missing upstream command; use `-- <cmd> [args...]`")
	}
	return a, nil
}

func parsePositiveInt(s string) (int, error) {
	var n int
	_, err := fmt.Sscanf(s, "%d", &n)
	return n, err
}

// ------------------ bridge ------------------

func runBridge(ctx context.Context, a *args) (int, error) {
	upstream := exec.CommandContext(ctx, a.upstreamCmd, a.upstreamArgs...)
	upstream.Stderr = os.Stderr

	stdin, err := upstream.StdinPipe()
	if err != nil {
		return 70, err
	}
	stdout, err := upstream.StdoutPipe()
	if err != nil {
		return 70, err
	}
	if err := upstream.Start(); err != nil {
		return 70, fmt.Errorf("spawn upstream: %w", err)
	}

	var logFile *os.File
	if a.logPath != "" {
		f, err := os.OpenFile(a.logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
		if err == nil {
			logFile = f
			defer f.Close()
		}
	}

	client := &http.Client{Timeout: a.timeout}

	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		pump(os.Stdin, stdin, "client-to-server", a, client, logFile)
		stdin.Close()
	}()
	go func() {
		defer wg.Done()
		pump(stdout, os.Stdout, "server-to-client", a, client, logFile)
	}()

	wg.Wait()
	if err := upstream.Wait(); err != nil {
		if exitErr, ok := err.(*exec.ExitError); ok {
			return exitErr.ExitCode(), nil
		}
		return 70, err
	}
	return 0, nil
}

func pump(in io.Reader, out io.Writer, direction string, a *args, client *http.Client, logFile io.Writer) {
	scanner := bufio.NewScanner(in)
	scanner.Buffer(make([]byte, 1024), 16*1024*1024)
	for scanner.Scan() {
		line := scanner.Bytes()
		if len(bytes.TrimSpace(line)) == 0 {
			continue
		}
		started := time.Now()

		var msg map[string]any
		if err := json.Unmarshal(line, &msg); err != nil {
			// Not JSON; forward raw.
			_, _ = out.Write(append(line, '\n'))
			continue
		}

		if !a.passthrough {
			translated, err := translate(client, a, direction, msg)
			if err != nil {
				fmt.Fprintf(os.Stderr, "[llmtrans-mcp-bridge] translate failed — forwarding untranslated: %v\n", err)
			} else if translated != nil {
				msg = translated
			}
		}

		encoded, err := json.Marshal(msg)
		if err != nil {
			_, _ = out.Write(append(line, '\n'))
			continue
		}
		_, _ = out.Write(append(encoded, '\n'))

		if logFile != nil {
			method, _ := msg["method"].(string)
			id := msg["id"]
			record := map[string]any{
				"ts":          time.Now().UTC().Format(time.RFC3339Nano),
				"direction":   direction,
				"method":      method,
				"id":          id,
				"elapsedMs":   time.Since(started).Milliseconds(),
				"bytes":       len(line),
				"passthrough": a.passthrough,
			}
			if b, err := json.Marshal(record); err == nil {
				_, _ = logFile.Write(append(b, '\n'))
			}
		}
	}
	if err := scanner.Err(); err != nil && err != io.EOF {
		fmt.Fprintf(os.Stderr, "[llmtrans-mcp-bridge] %s read error: %v\n", direction, err)
	}
}

func translate(client *http.Client, a *args, direction string, msg map[string]any) (map[string]any, error) {
	url := fmt.Sprintf("%s/mcp-translate/%s", a.endpoint, a.route)
	body, err := json.Marshal(map[string]any{"direction": direction, "message": msg})
	if err != nil {
		return nil, err
	}

	req, err := http.NewRequest(http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		raw, _ := io.ReadAll(io.LimitReader(resp.Body, 1024))
		return nil, fmt.Errorf("translate HTTP %d: %s", resp.StatusCode, strings.TrimSpace(string(raw)))
	}

	var envelope struct {
		Message map[string]any `json:"message"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&envelope); err != nil {
		return nil, err
	}
	return envelope.Message, nil
}

// ------------------ doctor ------------------

func doctor(a *args) error {
	client := &http.Client{Timeout: a.timeout}
	ok := true

	runCheck := func(name string, fn func() (string, error)) {
		t0 := time.Now()
		detail, err := fn()
		elapsed := time.Since(t0).Milliseconds()
		mark := "✓"
		if err != nil {
			mark = "✗"
			ok = false
			if detail == "" {
				detail = err.Error()
			}
		}
		fmt.Printf("  %s %-26s %5dms  %s\n", mark, name, elapsed, detail)
	}

	if ok {
		fmt.Println("llmtrans-mcp-bridge doctor:")
	}
	runCheck("endpoint reachable", func() (string, error) {
		resp, err := client.Get(a.endpoint + "/healthz")
		if err != nil {
			return "", err
		}
		defer resp.Body.Close()
		if resp.StatusCode >= 300 {
			return fmt.Sprintf("HTTP %d", resp.StatusCode), fmt.Errorf("status %d", resp.StatusCode)
		}
		return fmt.Sprintf("HTTP %d", resp.StatusCode), nil
	})
	runCheck("route token resolves", func() (string, error) {
		body, _ := json.Marshal(map[string]any{
			"direction": "server-to-client",
			"message":   map[string]any{"jsonrpc": "2.0", "id": 1, "result": map[string]any{"tools": []any{}}},
		})
		req, _ := http.NewRequest(http.MethodPost, a.endpoint+"/mcp-translate/"+a.route, bytes.NewReader(body))
		req.Header.Set("Content-Type", "application/json")
		resp, err := client.Do(req)
		if err != nil {
			return "", err
		}
		defer resp.Body.Close()
		if resp.StatusCode == http.StatusUnauthorized {
			return "HTTP 401 (token not recognized)", fmt.Errorf("401")
		}
		if resp.StatusCode >= 300 {
			return fmt.Sprintf("HTTP %d", resp.StatusCode), fmt.Errorf("status %d", resp.StatusCode)
		}
		return fmt.Sprintf("HTTP %d", resp.StatusCode), nil
	})

	if ok {
		fmt.Println("Result: PASS")
	} else {
		fmt.Println("Result: FAIL")
		return errors.New("one or more checks failed")
	}
	return nil
}

// ------------------ help ------------------

const helpText = `llmtrans-mcp-bridge — local stdio ↔ llmtrans translate-API bridge (Go)

USAGE
  llmtrans-mcp-bridge --route <token> [--endpoint <url>] [--passthrough] [--log <path>] -- <upstream-command> [args...]
  llmtrans-mcp-bridge doctor --route <token> [--endpoint <url>]

FLAGS
  --route, -r       Your llmtrans route token (required).
  --endpoint, -e    Base URL of the llmtrans API. Default: https://api.llmtrans.example.com
  --passthrough     Skip translation calls; forward stdio verbatim. Debugging aid.
  --log <path>      Append per-message timings to this file. Never logs body content.
  --timeout <ms>    Translate-API call timeout in ms. Default: 15000.
  --help, -h        Show this help.
  --version, -v     Print version and exit.

See the Node bridge README at ../LlmTrans.McpBridge/README.md for a full walkthrough.
`
