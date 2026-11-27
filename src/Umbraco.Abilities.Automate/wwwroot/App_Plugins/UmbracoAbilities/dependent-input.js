import { LitElement, html, css } from '@umbraco-cms/backoffice/external/lit';
import { UmbElementMixin } from '@umbraco-cms/backoffice/element-api';
import { UMB_PROPERTY_DATASET_CONTEXT } from '@umbraco-cms/backoffice/property';
import { UmbChangeEvent } from '@umbraco-cms/backoffice/event';
import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';

/**
 * FUDGE (dependent input): draws an input whose fields depend on other values in the same form, such as a page type's
 * fields once the page type is picked. Automate's form can't change with its own values, so this editor watches the
 * values it depends on, asks the Management API for the input's schema (POST abilities/{tool}/input-schema, as the
 * form's consumer), and lists the fields it can set. With a settings form that depends on its settings, this would go.
 *
 * It is a mapping, not the whole form: an automation runs with different data each time, so a step sets the few fields
 * that data fills, each from a binding to the trigger or an earlier step (${ … }) or a fixed value. Required fields are
 * always listed; the rest are added as needed. Everything else, blocks included, comes from the template the step
 * starts from, or stays empty. Its value is an object of field values, by alias.
 *
 * Config, from the automate-editors filter: tool (the tool's id), consumer, input (this property's alias) and dependsOn.
 */
export default class UmbAbilitiesDependentInputElement extends UmbElementMixin(LitElement) {
  static properties = {
    value: { type: Object },
    config: { attribute: false },
    _fields: { state: true },
    _message: { state: true },
    _added: { state: true },
    _modes: { state: true },
  };

  #dataset;
  #tool;
  #consumer;
  #input;
  #dependsOn = [];
  #chosen = {};
  #request = 0;
  _added = [];
  _modes = {};

  constructor() {
    super();
    this.consumeContext(UMB_PROPERTY_DATASET_CONTEXT, (dataset) => {
      this.#dataset = dataset;
      this.#watch();
    });
  }

  set config(config) {
    this.#tool = config?.getValueByAlias('tool');
    this.#consumer = config?.getValueByAlias('consumer');
    this.#input = config?.getValueByAlias('input');
    this.#dependsOn = config?.getValueByAlias('dependsOn') ?? [];
    this.#watch();
  }

  async #watch() {
    if (!this.#dataset || !this.#tool) {
      return;
    }

    for (const alias of this.#dependsOn) {
      const values = await this.#dataset.propertyValueByAlias(alias);
      if (!values) {
        continue;
      }

      this.observe(
        values,
        (value) => {
          // A dropdown gives a list of one.
          const chosen = Array.isArray(value) ? value[0] : value;
          if (this.#chosen[alias] === chosen) {
            return;
          }

          this.#chosen = { ...this.#chosen, [alias]: chosen };
          this.#load();
        },
        `abilities-chosen-${alias}`,
      );
    }

    this.#load();
  }

  async #load() {
    const request = ++this.#request;
    const chosen = Object.fromEntries(Object.entries(this.#chosen).filter(([, value]) => value !== undefined && value !== null && value !== ''));
    const { data, error } = await umbHttpClient.post({
      url: `/umbraco/management/api/v1/abilities/${encodeURIComponent(this.#tool)}/input-schema`,
      body: chosen,
      headers: { 'Content-Type': 'application/json', 'Umb-Consumer': this.#consumer },
      security: [{ scheme: 'bearer', type: 'http' }],
    });

    // Only the latest answer counts: an earlier one may arrive after it.
    if (request !== this.#request) {
      return;
    }

    const input = data?.properties?.[this.#input];
    if (error || !input?.properties) {
      this._fields = [];
      this._message = error
        ? 'The fields could not be loaded.'
        : `Choose the ${this.#dependsOn.filter((alias) => !(alias in chosen)).map(humanize).join(' or ').toLowerCase() || 'type'} above first.`;
      return;
    }

    const required = input.required ?? [];
    this._fields = Object.entries(input.properties)
      // Items, such as blocks, are many edits: a form of fixed fields can't build them.
      .filter(([, schema]) => !schema['x-item-types'])
      .map(([alias, schema]) => ({
        alias,
        label: humanize(alias),
        description: schema.description ?? '',
        editor: schema['x-editor']?.alias ?? 'Umb.PropertyEditorUi.TextBox',
        config: Object.entries(schema['x-editor']?.config ?? {}).map(([key, value]) => ({ alias: key, value })),
        mandatory: required.includes(alias),
        text: TEXT_EDITORS.includes(schema['x-editor']?.alias ?? 'Umb.PropertyEditorUi.TextBox'),
      }));
  }

  // The fields listed: the required ones, any with a value, and any just added.
  get #rows() {
    const value = this.value ?? {};
    return this._fields.filter((field) => field.mandatory || field.alias in value || this._added.includes(field.alias));
  }

  #set(alias, fieldValue) {
    this.value = { ...(this.value ?? {}), [alias]: fieldValue };
    this.dispatchEvent(new UmbChangeEvent());
  }

  #remove(alias) {
    const { [alias]: _, ...rest } = this.value ?? {};
    this._added = this._added.filter((added) => added !== alias);
    this.value = rest;
    this.dispatchEvent(new UmbChangeEvent());
  }

  #add(event) {
    const alias = event.target.value;
    if (alias) {
      this._added = [...this._added, alias];
      event.target.value = '';
    }
  }

  // A text field takes a binding or text in one box. Any other field takes a binding, or a fixed value in its editor.
  #mode(field) {
    if (field.text) {
      return 'bind';
    }

    const value = this.value?.[field.alias];
    return this._modes[field.alias] ?? (typeof value === 'string' && value.includes('${') ? 'bind' : value === undefined ? 'bind' : 'fixed');
  }

  #setMode(field, mode) {
    this._modes = { ...this._modes, [field.alias]: mode };
    this.#remove(field.alias);
    this._added = [...this._added, field.alias];
  }

  #fixedChanged(field, event) {
    event.stopPropagation();
    const entry = (event.target.value ?? []).find((e) => e.alias === field.alias);
    this.#set(field.alias, entry?.value);
  }

  #renderValue(field) {
    const value = this.value?.[field.alias];
    if (this.#mode(field) === 'bind') {
      return html`
        <uui-input
          label=${field.label}
          placeholder=${field.text ? 'Text, or ${ trigger.… }' : '${ trigger.… } or ${ steps.<step>.… }'}
          .value=${typeof value === 'string' ? value : ''}
          @input=${(e) => this.#set(field.alias, e.target.value)}
        ></uui-input>
      `;
    }

    return html`
      <umb-property-dataset .value=${[{ alias: field.alias, value }]} @change=${(e) => this.#fixedChanged(field, e)}>
        <umb-property
          alias=${field.alias}
          label=${field.label}
          property-editor-ui-alias=${field.editor}
          .config=${field.config}
        ></umb-property>
      </umb-property-dataset>
    `;
  }

  render() {
    if (this._fields === undefined) {
      return html`<uui-loader></uui-loader>`;
    }

    if (this._fields.length === 0) {
      return html`<p class="message">${this._message}</p>`;
    }

    const rows = this.#rows;
    const rest = this._fields.filter((field) => !rows.includes(field));
    return html`
      <p class="message">Set only the fields this step fills. The rest come from the template, or stay as they are.</p>
      <div class="rows">
        ${rows.map(
          (field) => html`
            <div class="row">
              <div class="label">
                <strong>${field.label}${field.mandatory ? html`<span class="required" aria-label="required">*</span>` : ''}</strong>
                ${field.description ? html`<small>${field.description}</small>` : ''}
              </div>
              <div class="value">
                ${field.text
                  ? ''
                  : html`
                      <uui-button-group>
                        <uui-button compact look=${this.#mode(field) === 'bind' ? 'primary' : 'secondary'} label="From the run" @click=${() => this.#setMode(field, 'bind')}></uui-button>
                        <uui-button compact look=${this.#mode(field) === 'fixed' ? 'primary' : 'secondary'} label="Fixed value" @click=${() => this.#setMode(field, 'fixed')}></uui-button>
                      </uui-button-group>
                    `}
                ${this.#renderValue(field)}
              </div>
              ${field.mandatory
                ? html`<span></span>`
                : html`<uui-button compact label="Remove" @click=${() => this.#remove(field.alias)}><uui-icon name="icon-trash"></uui-icon></uui-button>`}
            </div>
          `,
        )}
      </div>
      ${rest.length > 0
        ? html`
            <uui-select
              label="Add a field"
              .options=${[{ name: 'Add a field…', value: '', selected: true }, ...rest.map((field) => ({ name: field.label, value: field.alias }))]}
              @change=${this.#add}
            ></uui-select>
          `
        : ''}
    `;
  }

  static styles = css`
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--uui-size-space-4);
    }

    .rows {
      display: flex;
      flex-direction: column;
      gap: var(--uui-size-space-3);
    }

    .row {
      display: grid;
      grid-template-columns: minmax(120px, 1fr) 2fr auto;
      gap: var(--uui-size-space-4);
      align-items: start;
      padding-bottom: var(--uui-size-space-3);
      border-bottom: 1px solid var(--uui-color-divider);
    }

    .label {
      display: flex;
      flex-direction: column;
    }

    .label small {
      color: var(--uui-color-text-alt);
    }

    .required {
      color: var(--uui-color-invalid);
      margin-left: 2px;
    }

    .value {
      display: flex;
      flex-direction: column;
      gap: var(--uui-size-space-2);
    }

    uui-input {
      width: 100%;
    }

    .message {
      margin: 0;
      color: var(--uui-color-text-alt);
      font-style: italic;
    }
  `;
}

// Editors whose value is plain text, so one box takes text or a binding.
const TEXT_EDITORS = ['Umb.PropertyEditorUi.TextBox', 'Umb.PropertyEditorUi.TextArea'];

// "urlSegment" → "Url segment".
function humanize(alias) {
  const words = alias.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

customElements.define('umb-abilities-dependent-input', UmbAbilitiesDependentInputElement);
