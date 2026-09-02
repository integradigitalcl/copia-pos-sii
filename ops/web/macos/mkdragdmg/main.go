// mkdragdmg builds a macOS DMG with a single .app and an Applications symlink
// (classic drag-to-Applications layout). Forces Unix +x on launchers/binaries
// because Windows source trees do not preserve executable bits for mkdmg.
package main

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/leaanthony/dmg/dsstore"
	"github.com/leaanthony/dmg/hfsplus"
	"github.com/leaanthony/dmg/udif"
)

func main() {
	if len(os.Args) != 3 {
		fmt.Fprintf(os.Stderr, "uso: mkdragdmg <Grunflex POS Web.app> <salida.dmg>\n")
		os.Exit(2)
	}
	appPath, outPath := os.Args[1], os.Args[2]
	appName := filepath.Base(appPath)

	appEntry, err := pathToEntry(appName, appPath)
	if err != nil {
		fatal(err)
	}
	fixExecModes(appEntry, "")

	vol := &hfsplus.Volume{
		Name:      "Grunflex POS Web",
		CreatedAt: time.Now(),
		Children: []*hfsplus.Entry{
			appEntry,
			{Name: "Applications", Kind: hfsplus.KindSymlink, Target: "/Applications"},
		},
	}

	ds, err := buildDSStore(appName)
	if err != nil {
		fatal(err)
	}
	vol.Children = append(vol.Children, &hfsplus.Entry{Name: ".DS_Store", Kind: hfsplus.KindFile, Data: ds})

	img, err := hfsplus.Build(vol)
	if err != nil {
		fatal(err)
	}

	_ = os.Remove(outPath)
	f, err := os.Create(outPath)
	if err != nil {
		fatal(err)
	}
	if err := udif.Build(f, img, "whole disk (Apple_HFS : 0)"); err != nil {
		f.Close()
		_ = os.Remove(outPath)
		fatal(err)
	}
	if err := f.Close(); err != nil {
		fatal(err)
	}
	fmt.Println("DMG:", outPath)
}

func fatal(err error) {
	fmt.Fprintln(os.Stderr, err)
	os.Exit(1)
}

func buildDSStore(appName string) ([]byte, error) {
	s := dsstore.New()
	s.SetViewStyle(dsstore.ViewIcon)
	s.SetWindowPosition(120, 120, 120+420, 120+640) // top, left, bottom, right
	s.SetWindowSettings(dsstore.WindowSettings{
		Top: 120, Left: 120, Bottom: 120 + 420, Right: 120 + 640,
	})
	s.SetIconViewOptions(dsstore.IconViewOptions{
		IconSize:         128,
		TextSize:         12,
		GridSpace:        100,
		LabelOnBottom:    true,
		ShowIconPreview:  true,
		BackgroundType:   dsstore.BackgroundColor,
		BackgroundRed:    0.95,
		BackgroundGreen:  0.95,
		BackgroundBlue:   0.97,
	})
	s.SetIconPosition(appName, 160, 200)
	s.SetIconPosition("Applications", 460, 200)
	var buf bytes.Buffer
	if err := s.WriteTo(&buf); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

func pathToEntry(name, src string) (*hfsplus.Entry, error) {
	fi, err := os.Lstat(src)
	if err != nil {
		return nil, err
	}
	switch {
	case fi.Mode()&os.ModeSymlink != 0:
		target, err := os.Readlink(src)
		if err != nil {
			return nil, err
		}
		return &hfsplus.Entry{Name: name, Kind: hfsplus.KindSymlink, Target: target}, nil
	case fi.IsDir():
		des, err := os.ReadDir(src)
		if err != nil {
			return nil, err
		}
		kids := make([]*hfsplus.Entry, 0, len(des))
		for _, de := range des {
			ent, err := pathToEntry(de.Name(), filepath.Join(src, de.Name()))
			if err != nil {
				return nil, err
			}
			kids = append(kids, ent)
		}
		return &hfsplus.Entry{Name: name, Kind: hfsplus.KindDir, Children: kids}, nil
	default:
		data, err := os.ReadFile(src)
		if err != nil {
			return nil, err
		}
		return &hfsplus.Entry{Name: name, Kind: hfsplus.KindFile, Data: data, Mode: 0644}, nil
	}
}

func fixExecModes(e *hfsplus.Entry, rel string) {
	path := rel
	if path == "" {
		path = e.Name
	} else {
		path = rel + "/" + e.Name
	}
	switch e.Kind {
	case hfsplus.KindDir:
		for _, c := range e.Children {
			fixExecModes(c, path)
		}
	case hfsplus.KindFile:
		if shouldBeExecutable(path, e.Data) {
			e.Mode = 0755
		}
	}
}

func shouldBeExecutable(path string, data []byte) bool {
	base := filepath.Base(path)
	lower := strings.ToLower(path)
	if strings.Contains(lower, "/contents/macos/") {
		return true
	}
	if strings.HasSuffix(lower, ".sh") || strings.HasSuffix(lower, ".command") {
		return true
	}
	if base == "GrunflexPOS.Web" || base == "GrunflexPOS.API" || base == "GrunflexPOSWeb" {
		return true
	}
	if len(data) >= 2 && data[0] == '#' && data[1] == '!' {
		return true
	}
	// Mach-O magic (thin/fat)
	if len(data) >= 4 {
		m := uint32(data[0])<<24 | uint32(data[1])<<16 | uint32(data[2])<<8 | uint32(data[3])
		switch m {
		case 0xFEEDFACE, 0xFEEDFACF, 0xCEFAEDFE, 0xCFFAEDFE, 0xCAFEBABE:
			return true
		}
	}
	return false
}
