import { LitElement, html, css } from '@umbraco-cms/backoffice/external/lit';
import { UmbElementMixin } from '@umbraco-cms/backoffice/element-api';
import { UMB_PROPERTY_DATASET_CONTEXT } from '@umbraco-cms/backoffice/property';
import { UmbChangeEvent } from '@umbraco-cms/backoffice/event';
import { umbHttpClient } from '@umbraco-cms/backoffice/http-client';

/**
 * FUDGE (dependent input): draws an input whose fields depend on other values in the same form, such as a page type's
 * fields once the page type is picked. Automate's form can't change with its own values, so this editor watches the
 * values it depends on, asks the Management API for the input's schema (POST abilities/{tool}/input-schema, as the
 * form's consumer), and draws each field with the editor its schema names (x-editor). Its value is an object of field
 * values, by alias, as each editor gives them. With a settings form that depends on its settings, this would go.
 *
 * Config, from the automate-editors filter: tool (the tool's id), consumer, input (this property's alias) and dependsOn.
 */
export default class UmbAbilitiesDependentInputElement extends UmbElementMixin(LitElement) {
  static properties = {
    value: { type: Object },
    config: { attribute: false },
    _fields: { state: true },
    _message: { state: true },
  };

  #dataset;
  #tool;
  #consumer;
  #input;
  #dependsOn = [];
  #chosen = {};
  #request = 0;

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
        : `Choose the ${this.#dependsOn.filter((alias) => !(alias in chosen)).map(humanize).join(' and ').toLowerCase() || 'type'} above first.`;
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
      }));
  }

  #changed(event) {
    event.stopPropagation();
    this.value = Object.fromEntries((event.target.value ?? []).map((entry) => [entry.alias, entry.value]));
    this.dispatchEvent(new UmbChangeEvent());
  }

  render() {
    if (this._fields === undefined) {
      return html`<uui-loader></uui-loader>`;
    }

    if (this._fields.length === 0) {
      return html`<p class="message">${this._message}</p>`;
    }

    const values = Object.entries(this.value ?? {}).map(([alias, value]) => ({ alias, value }));
    return html`
      <umb-property-dataset .value=${values} @change=${this.#changed}>
        ${this._fields.map(
          (field) => html`
            <umb-property
              alias=${field.alias}
              label=${field.label}
              description=${field.description}
              property-editor-ui-alias=${field.editor}
              .config=${field.config}
              .validation=${{ mandatory: field.mandatory }}
            ></umb-property>
          `,
        )}
      </umb-property-dataset>
    `;
  }

  static styles = css`
    :host {
      display: block;
    }

    .message {
      margin: 0;
      color: var(--uui-color-text-alt);
      font-style: italic;
    }
  `;
}

// "urlSegment" → "Url segment".
function humanize(alias) {
  const words = alias.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

customElements.define('umb-abilities-dependent-input', UmbAbilitiesDependentInputElement);
